/**
 * session-manager.mjs — inventory, inspect, archive, delete and repair DSH sessions.
 *
 * WHY THIS LIVES IN NODE, NOT IN THE C# LAUNCHER
 * ----------------------------------------------
 * Session logs are zstd concatenated-frame containers. .NET has no built-in zstd
 * and this machine is offline for NuGet, while Node ships one. More importantly
 * the frame walker in dsh-session-log.mjs is already verified against real logs,
 * and the launcher must never disagree with it about what a session contains.
 * So this file owns the logic and speaks JSON on stdout; the WPF UI only renders.
 *
 * WHAT A SESSION DIRECTORY ACTUALLY CONTAINS
 * ------------------------------------------
 *   sessions/<encoded-workspace>/session-<uuid>/  -> a USER session
 *   sessions/<encoded-workspace>/<bare-uuid>/     -> a SUB-AGENT session
 *   sessions/<encoded-workspace>/import-session-* -> an imported session
 *   sessions/_no-cwd/<name>/                      -> a system pseudo-session
 * Only the first kind belongs in a user's session list. The others are what
 * makes the list look polluted, so they are classified explicitly rather than
 * discovered by accident.
 *
 * ARCHIVE STATE IS DSH'S, NOT OURS
 * --------------------------------
 * DSH hides archived sessions, and it records them in
 * `~/.dsh/storages/workspace.json` -> global.archivedSessionIds. Hiding a session
 * therefore means editing that list, which makes DSH itself hide it — no shadow
 * state that could drift out of sync.
 *
 * EVERY DESTRUCTIVE ACTION MOVES, NEVER ERASES
 * --------------------------------------------
 * Delete moves the session directory into a quarantine folder with a manifest.
 * A tool that can destroy months of transcript with one typo has to be
 * recoverable; `purge` is a separate, explicit second step.
 *
 * CLI (all output is JSON on stdout, diagnostics on stderr):
 *   list [--include-subagent] [--include-imported] [--include-archived] [--all]
 *   show --id <id> [--tail N] [--user-only]
 *   scan                        health of every log
 *   archive   --id <id> [...]   hide in DSH
 *   unarchive --id <id> [...]
 *   delete    --id <id> [...]   move to quarantine
 *   restore   --manifest <path> bring a quarantine batch back
 *   purge     --id <id> [...]   DESTROY live sessions outright (irreversible)
 *   purge     --kind subagent|imported|emptied   destroy quarantined items
 *   repair    --id <id> [...]   drop an unreadable trailer (backs up first)
 */

import { existsSync, mkdirSync, readdirSync, readFileSync, renameSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { basename, dirname, join } from 'node:path';
import { readSession, readFrames, repairSession, renumberSessionSeq } from './dsh-session-log.mjs';

const DSH_HOME = process.env.DSH_HOME || join(homedir(), '.dsh');
const SESSIONS_ROOT = join(DSH_HOME, 'sessions');
const WORKSPACE_JSON = join(DSH_HOME, 'storages', 'workspace.json');
const QUARANTINE_ROOT = join(DSH_HOME, 'supervision', 'session-quarantine');

export const SESSION_KIND = {
  user: 'user',
  subagent: 'subagent',
  imported: 'imported',
  system: 'system',
};

// ── metadata index ───────────────────────────────────────────────────────────
/**
 * Decoding a session log is expensive: the frame walker must decompress every
 * zstd frame and JSON.parse every line. This machine holds ~86 logs totalling
 * ~187 MB, and `list` used to decode ALL of them on every invocation — and a
 * bare-uuid directory was decoded TWICE (once to classify, once to describe).
 * That is a ~19 s stall, paid again after every single archive / delete / repair,
 * which is what made "modify a session" feel slow when the mutation itself takes
 * 60 ms.
 *
 * Everything learned from the bytes (kind, title, cwd, counts, health, empty) is
 * a pure function of those bytes, so it is memoised against (size, mtimeMs). An
 * unchanged log is never decoded twice, and a mutation invalidates exactly the
 * one file it touched. Deleted sessions are pruned on the next walk.
 *
 * The index is a CACHE, never a source of truth: any stat mismatch, parse
 * failure, version bump or missing file silently falls back to decoding.
 */
const INDEX_VERSION = 3;
const INDEX_PATH = process.env.DSH_SESSION_INDEX
  || join(process.env.LOCALAPPDATA || homedir(), 'DshLauncher', 'session-index.json');

// Read straight from argv: this module-level constant is initialised before the
// `flag()` helper exists, so it must not depend on it.
const INDEX_DISABLED = process.argv.includes('--no-index');

function loadIndex() {
  const empty = { version: INDEX_VERSION, entries: {}, dirty: false, hits: 0, misses: 0 };
  if (INDEX_DISABLED) return empty;
  try {
    const raw = JSON.parse(readFileSync(INDEX_PATH, 'utf8'));
    if (raw?.version !== INDEX_VERSION || !raw.entries) return empty;
    return { version: INDEX_VERSION, entries: raw.entries, dirty: false, hits: 0, misses: 0 };
  } catch {
    return empty;   // first run, or unreadable — rebuild from scratch
  }
}

const INDEX = loadIndex();

function saveIndex() {
  if (!INDEX.dirty || INDEX_DISABLED) return;
  try {
    mkdirSync(dirname(INDEX_PATH), { recursive: true });
    const tmp = `${INDEX_PATH}.tmp-${process.pid}`;
    writeFileSync(tmp, JSON.stringify(INDEX), 'utf8');
    renameSync(tmp, INDEX_PATH);   // temp+rename: an interrupted write cannot corrupt it
  } catch (e) {
    warn(`[index] cache not persisted (harmless, just slower next time): ${e.message}`);
  }
}
// The CLI is a one-shot process, so exit is the natural commit point.
process.on('exit', saveIndex);

/** Drop entries whose session directory is gone, so the file cannot grow forever. */
function pruneIndex(liveLogPaths) {
  for (const key of Object.keys(INDEX.entries)) {
    if (!liveLogPaths.has(key)) { delete INDEX.entries[key]; INDEX.dirty = true; }
  }
}

const argv = process.argv.slice(2);
const cmd = argv[0];
const flag = (name) => argv.includes(name);
function opt(name, dflt = null) {
  const i = argv.indexOf(name);
  return i >= 0 && argv[i + 1] ? argv[i + 1] : dflt;
}
function optList(name) {
  const out = [];
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === name) {
      for (let j = i + 1; j < argv.length && !argv[j].startsWith('--'); j += 1) out.push(argv[j]);
    }
  }
  return out;
}

const emit = (obj) => process.stdout.write(JSON.stringify(obj, null, 2) + '\n');
const warn = (...a) => process.stderr.write(a.join(' ') + '\n');

// ── archive state (DSH's own) ────────────────────────────────────────────────
function readWorkspaceState() {
  if (!existsSync(WORKSPACE_JSON)) {
    return { raw: null, archived: new Set(), listed: new Set(), titles: {} };
  }
  const raw = JSON.parse(readFileSync(WORKSPACE_JSON, 'utf8'));
  const archived = new Set(raw.global?.archivedSessionIds ?? []);
  const listed = new Set();
  const titles = {};
  for (const [id, w] of Object.entries(raw.tables?.workspaces ?? {})) {
    titles[id] = { path: w.path ?? null, title: w.title ?? null };
    for (const sid of (w.sessionIds ?? [])) listed.add(sid);
  }
  return { raw, archived, listed, titles };
}

function writeWorkspaceArchived(raw, archivedSet) {
  const next = JSON.parse(JSON.stringify(raw));
  next.global = next.global ?? {};
  next.global.archivedSessionIds = [...archivedSet];
  // Write via a temp file + rename so an interrupted write cannot destroy the
  // file that DSH uses to decide what is hidden.
  const tmp = `${WORKSPACE_JSON}.tmp-${process.pid}`;
  writeFileSync(tmp, JSON.stringify(next), 'utf8');
  renameSync(tmp, WORKSPACE_JSON);
}

// ── inventory ────────────────────────────────────────────────────────────────
/** Name-only classification. Returns null when only the log itself can decide. */
function classifyByName(dirName) {
  if (dirName === '_no-cwd' || dirName.startsWith('_')) return SESSION_KIND.system;
  if (dirName.startsWith('session-')) return SESSION_KIND.user;
  if (dirName.startsWith('import-session-')) return SESSION_KIND.imported;
  return null;   // bare uuid (or anything else): needs the log
}

/**
 * Everything that can only be learned by decoding the log, computed in ONE pass
 * and memoised against (size, mtimeMs). `st` is the stat of the log, or null
 * when it does not exist. Returns null in that case.
 *
 * This replaces what used to be two separate full decodes of the same file
 * (classify -> readSession, describe -> readSession).
 */
function logFacts(entry, st) {
  if (!st) return null;

  const hit = INDEX.entries[entry.logPath];
  if (hit && hit.size === st.size && hit.mtimeMs === st.mtimeMs) {
    INDEX.hits += 1;
    return hit.facts;
  }
  INDEX.misses += 1;

  const named = classifyByName(entry.name);
  let sess;
  try {
    sess = readSession(entry.logPath);
  } catch (e) {
    const facts = { kind: named ?? SESSION_KIND.system, health: 'unreadable', error: e.message };
    INDEX.entries[entry.logPath] = { size: st.size, mtimeMs: st.mtimeMs, facts };
    INDEX.dirty = true;
    return facts;
  }

  const d = sess.events.find((e) => e.type === 'subagent/descriptor');
  const facts = {
    // A bare uuid under a workspace is how DSH stores a delegated run; confirm it
    // rather than assume, because a half-written directory can also look bare.
    kind: named ?? (d ? SESSION_KIND.subagent : SESSION_KIND.system),
    title: sess.events.find((e) => e.type === 'session/title')?.data?.title ?? null,
    cwd: sess.cwd,
    createdAt: sess.createdAt ? new Date(sess.createdAt).toISOString() : null,
    preset: sess.agentPreset,
    health: sess.ok ? 'ok' : 'damaged',
    corruption: sess.corruption,
    counts: {
      events: sess.stats.events,
      userMessages: sess.eventCounts['user/message'] ?? 0,
      assistantMessages: sess.eventCounts['assistant/message'] ?? 0,
      toolCalls: sess.eventCounts['tool/call'] ?? 0,
    },
    // Sessions that never produced an assistant message are almost always
    // abandoned attempts; surfacing the flag lets the user clean them up.
    empty: (sess.eventCounts['assistant/message'] ?? 0) === 0,
  };
  if (facts.kind === SESSION_KIND.subagent) {
    facts.subagentLabel = d?.data?.label ?? null;
    facts.subagentMode = d?.data?.mode ?? null;
  }

  INDEX.entries[entry.logPath] = { size: st.size, mtimeMs: st.mtimeMs, facts };
  INDEX.dirty = true;
  return facts;
}

/** Kind for one directory, resolving bare uuids through the index when possible. */
function kindOfEntry(entry) {
  const named = classifyByName(entry.name);
  if (named) return named;
  const hasLog = existsSync(entry.logPath);
  return logFacts(entry, hasLog ? statSync(entry.logPath) : null)?.kind ?? SESSION_KIND.system;
}

function* walk() {
  if (!existsSync(SESSIONS_ROOT)) return;
  for (const ws of readdirSync(SESSIONS_ROOT, { withFileTypes: true })) {
    if (!ws.isDirectory()) continue;
    for (const d of readdirSync(join(SESSIONS_ROOT, ws.name), { withFileTypes: true })) {
      if (!d.isDirectory()) continue;
      const dir = join(SESSIONS_ROOT, ws.name, d.name);
      yield { workspace: ws.name, name: d.name, dir, logPath: join(dir, 'session.jsonl.zstd'), extra: join(dir, 'session.json') };
    }
  }
}

function describe(entry, wsState, { readHeader = true } = {}) {
  const hasLog = existsSync(entry.logPath);
  const st = hasLog ? statSync(entry.logPath) : null;   // one stat, not three
  const named = classifyByName(entry.name);

  const rec = {
    id: entry.name,
    workspace: entry.workspace,
    kind: named,                       // resolved below when only the log knows
    dir: entry.dir,
    logPath: entry.logPath,
    hasLog,
    archived: wsState.archived.has(entry.name),
    listedInWorkspace: wsState.listed.has(entry.name),
    bytes: st ? st.size : 0,
    modified: st ? st.mtime.toISOString() : null,
  };

  if (!hasLog) { rec.kind ??= SESSION_KIND.system; return rec; }

  if (!readHeader) {
    // Cheap path: a name-classifiable directory needs NO decode at all. Only a
    // bare uuid has to consult the log, and the index usually answers that.
    rec.kind = named ?? logFacts(entry, st)?.kind ?? SESSION_KIND.system;
    return rec;
  }

  const facts = logFacts(entry, st);
  if (facts) Object.assign(rec, facts);
  rec.kind ??= SESSION_KIND.system;
  return rec;
}

/**
 * A session is a PROBE when its first user message is an instruction addressed to
 * a tool rather than a human request. Automated acceptance runs (headless
 * one-shot profiles) produce these by the dozen, and they land in the user's own
 * session list looking like real conversations — which is exactly the pollution
 * this selector exists to clear.
 */
export function looksLikeProbe(firstUserText) {
  const t = String(firstUserText ?? '').trim();
  return /^(请)?(先)?调用\s+[a-z_]+|^Call the [a-z_]+ tool|^You must call\b/i.test(t);
}

/**
 * Classify an inbox-injected text.
 *
 * The agent inbox is a SHARED channel. Enumerating all 1887 injected texts in
 * this machine's corpus shows it carries three unrelated things:
 *
 *   'user'      real user messages typed while the agent was running — short
 *               interjections like 继续 / 检查 / 还是不行, and full requests such
 *               as "在当前文件下重新部署一个 deepseek Harness…". These MUST survive
 *               any filtering; dropping them deletes the user's own words.
 *   'system'    machine notices: the repeated-tool-call nudge, approval-policy
 *               changes, and the "Your parent agent id is …" declaration.
 *   'delegated' work handed to another agent: ROLE:, 你是**独立验收 Agent**,
 *               你的任务：, You must …
 *
 * Filtering on "arrived via the inbox" alone collapses all three and silently
 * destroys the first category, which is why classification is by shape.
 */
export function classifyInboxText(text) {
  const t = String(text ?? '').trim();
  if (!t) return 'user';

  if (/^You are repeating the exact same tool call/i.test(t)) return 'system';
  if (/^The approval policy changed/i.test(t)) return 'system';
  if (/^Your parent agent id is\b/i.test(t)) return 'system';
  if (/^Your previous (response|message) was (cut|truncated)/i.test(t)) return 'system';

  if (/^(ROLE|AGENT|TASK)\s*[:：]/i.test(t)) return 'delegated';
  if (/^You (are|must|should)\b/.test(t)) return 'delegated';
  if (/^你是[「『"]?(独立|执行|审查|验收|核验|修复|校验)/.test(t)) return 'delegated';
  if (/^你的任务[:：]/.test(t)) return 'delegated';
  if (/^你负责/.test(t)) return 'delegated';
  if (/^(任务书|角色)[:：]/.test(t)) return 'delegated';

  return 'user';
}

/** Kept for callers that only need the boolean. */
export function looksLikeDelegatedTask(text) {
  return classifyInboxText(text) === 'delegated';
}

function firstUserText(entry) {
  try {
    const sess = readSession(entry.logPath);
    return sess.events.find((e) => e.type === 'user/message')?.data?.content?.[0]?.text ?? '';
  } catch {
    return '';
  }
}

const SELECTORS = ['subagent', 'probe', 'imported', 'system', 'unlisted', 'archived', 'empty', 'all'];

/** Resolve `--select <class>` into concrete session ids, so bulk work needs no id list. */
function selectIds(selector) {
  if (!SELECTORS.includes(selector)) {
    throw new Error(`unknown --select "${selector}"; expected one of ${SELECTORS.join(', ')}`);
  }
  const wsState = readWorkspaceState();
  const hits = [];
  for (const e of walk()) {
    const rec = describe(e, wsState, { readHeader: false });
    let match = false;
    switch (selector) {
      case 'subagent': match = rec.kind === SESSION_KIND.subagent; break;
      case 'imported': match = rec.kind === SESSION_KIND.imported; break;
      case 'system':   match = rec.kind === SESSION_KIND.system; break;
      case 'archived': match = rec.archived; break;
      case 'unlisted': match = !rec.listedInWorkspace; break;
      case 'all':      match = true; break;
      case 'probe':
      case 'empty': {
        if (!rec.hasLog) break;
        const full = describe(e, wsState);
        match = selector === 'probe'
          ? looksLikeProbe(firstUserText(e))
          : Boolean(full.empty);
        break;
      }
      default: break;
    }
    if (match) hits.push(rec.id);
  }
  return hits;
}

function listSessions() {  const wsState = readWorkspaceState();
  const all = [];
  const livePaths = new Set();
  for (const e of walk()) {
    livePaths.add(e.logPath);
    all.push(describe(e, wsState));
  }
  pruneIndex(livePaths);

  const includeSub = flag('--all') || flag('--include-subagent');
  const includeImp = flag('--all') || flag('--include-imported');
  const includeArc = flag('--all') || flag('--include-archived');
  const includeSys = flag('--all');

  const shown = all.filter((r) => {
    if (r.kind === SESSION_KIND.subagent && !includeSub) return false;
    if (r.kind === SESSION_KIND.imported && !includeImp) return false;
    if (r.kind === SESSION_KIND.system && !includeSys) return false;
    if (r.archived && !includeArc) return false;
    return true;
  });

  const byKind = {};
  for (const r of all) byKind[r.kind] = (byKind[r.kind] ?? 0) + 1;

  return {
    ok: true,
    root: SESSIONS_ROOT,
    totals: {
      all: all.length,
      shown: shown.length,
      byKind,
      archived: all.filter((r) => r.archived).length,
      damaged: all.filter((r) => r.health && r.health !== 'ok').length,
      unlisted: all.filter((r) => !r.listedInWorkspace).length,
    },
    // DSH's archive list can reference sessions whose directory is gone; those are
    // reported instead of silently ignored, because they are why DSH may show
    // gaps that look like data loss.
    staleArchiveRefs: [...wsState.archived].filter((id) => !all.some((r) => r.id === id)),
    // How much work this call avoided. misses == number of logs actually decoded.
    index: { hits: INDEX.hits, misses: INDEX.misses, cached: Object.keys(INDEX.entries).length },
    // A session with no log has a null `modified`; sorting on the raw value put
    // those in an arbitrary place, so they are pinned last.
    sessions: shown.sort((a, b) => {
      const am = a.modified ?? '', bm = b.modified ?? '';
      if (am === bm) return 0;
      if (!am) return 1;
      if (!bm) return -1;
      return bm.localeCompare(am);
    }),
  };
}

// ── transcript ───────────────────────────────────────────────────────────────
/**
 * Render a session as a readable transcript.
 *
 * `--user-only` drops the delegated runs: a sub-agent run writes its task text
 * into the parent's inbox and its work into the parent's turns, so without this
 * the parent view reads as if the user had asked for the sub-agent's assignment.
 */
function showSession(id, { tail = 0, userOnly = false } = {}) {
  const wsState = readWorkspaceState();
  let target = null;
  for (const e of walk()) if (e.name === id) { target = describe(e, wsState); break; }
  if (!target) return { ok: false, error: `no session with id ${id}` };
  if (!target.hasLog) return { ok: false, error: `${id} has no log file`, session: target };

  const sess = readSession(target.logPath);
  const turns = [];

  // Which user messages were injected rather than authored?
  //
  // `agent/inbox/spliced` carries the injected content, and that exact text later
  // appears as a user/message event — but the splice event's `seq` is the seq of
  // the SPLICE, not of the resulting message, so correlating by seq silently
  // matches nothing (verified: 461 splices, zero matches). The association has to
  // be made by text.
  const splicedTexts = [];
  for (const e of sess.events) {
    if (e.type !== 'agent/inbox/spliced') continue;
    for (const item of (e.data?.inserted ?? [])) {
      for (const c of (item?.content ?? [])) {
        if (typeof c?.text === 'string' && c.text.trim()) splicedTexts.push(c.text.trim());
      }
    }
  }
  const norm = (s) => String(s ?? '').trim().replace(/\s+/g, ' ');
  const splicedSet = new Set(splicedTexts.map(norm));
  const HEAD = 160;
  const isInjected = (text) => {
    const n = norm(text);
    if (!n) return false;
    if (splicedSet.has(n)) return true;
    // The logged message is often a truncated or lightly wrapped form of the
    // injected text, so compare generous prefixes instead of demanding equality.
    return splicedTexts.some((t) => {
      const tn = norm(t);
      if (tn.length < 30 || n.length < 30) return false;
      return tn.slice(0, HEAD) === n.slice(0, HEAD);
    });
  };

  let current = null;
  for (const e of sess.events) {
    const t = e.data?.turn;
    if (e.type === 'turn/start') { current = { turn: t, entries: [] }; turns.push(current); continue; }
    if (!current) continue;
    if (e.type === 'user/message') {
      const text = (e.data?.content ?? []).map((c) => c.text ?? '').join('\n');
      const viaInbox = isInjected(text);
      // Provenance + nature, so the UI can show "this arrived through the inbox"
      // without treating it as "not the user's".
      const kind = viaInbox ? classifyInboxText(text) : 'user';
      current.entries.push({ role: 'user', seq: e.seq, time: e.time, text, viaInbox, inboxKind: kind, delegated: kind === 'delegated' });
    } else if (e.type === 'assistant/message') {
      const text = (e.data?.message?.content ?? []).filter((c) => c?.type === 'text').map((c) => c.text ?? '').join('\n');
      if (text.trim()) current.entries.push({ role: 'assistant', seq: e.seq, time: e.time, text });
    } else if (e.type === 'tool/call') {
      current.entries.push({ role: 'tool', seq: e.seq, time: e.time, name: e.data?.name, args: e.data?.arguments });
    } else if (e.type === 'todo/write') {
      current.entries.push({ role: 'plan', seq: e.seq, time: e.time, todos: e.data?.todos ?? [] });
    }
  }

  // A turn is dropped by --user-only only when its user input is machine noise
  // or another agent's task order. A turn carrying the user's own interjection
  // is kept, even though it arrived through the same inbox.
  const kindOfTurn = (t) => {
    const users = t.entries.filter((x) => x.role === 'user');
    if (users.length === 0) return 'none';
    if (users.some((x) => x.inboxKind === 'user' || x.inboxKind === undefined)) return 'user';
    if (users.some((x) => x.inboxKind === 'delegated')) return 'delegated';
    return 'system';
  };

  const delegatedTurns = turns.filter((t) => kindOfTurn(t) === 'delegated').length;
  const systemTurns = turns.filter((t) => kindOfTurn(t) === 'system').length;

  let out = turns;
  if (userOnly) out = turns.filter((t) => { const k = kindOfTurn(t); return k === 'user' || k === 'none'; });
  if (tail > 0) out = out.slice(-tail);

  return {
    ok: true,
    session: {
      id: target.id, title: target.title, cwd: target.cwd, kind: target.kind,
      createdAt: target.createdAt, archived: target.archived, health: target.health,
      bytes: target.bytes, subagentLabel: target.subagentLabel,
    },
    corruption: sess.corruption,
    eventCounts: sess.eventCounts,
    subagentSpliced: {
      descriptors: sess.events.filter((e) => e.type === 'subagent/descriptor').length,
      inboxSplices: sess.events.filter((e) => e.type === 'agent/inbox/spliced').length,
      injectedTexts: splicedTexts.length,
      // Reported separately rather than collapsed: inboxTurns counts provenance,
      // delegatedTurns and systemTurns count what the content actually is. Only
      // the latter two are removed by --user-only.
      inboxTurns: turns.filter((t) => t.entries.some((x) => x.role === 'user' && x.viaInbox)).length,
      delegatedTurns,
      systemTurns,
    },
    turnCount: out.length,
    turns: out,
  };
}

// ── health scan ──────────────────────────────────────────────────────────────
function scanAll() {
  const wsState = readWorkspaceState();
  const rows = [];
  for (const e of walk()) {
    if (!existsSync(e.logPath)) { rows.push({ id: e.name, kind: kindOfEntry(e), health: 'missing-log' }); continue; }
    try {
      const w = readFrames(e.logPath);
      const sess = readSession(e.logPath);
      // Structural LOAD-BLOCKING damage: only duplicate or out-of-order seq.
      // openTurns / unansweredToolCalls are reported but are NOT load-blocking
      // (a killed session still loads), so they must not flip health here.
      const structBad = sess.structural && (
        sess.structural.duplicateSeq.length > 0 ||
        sess.structural.outOfOrderSeq > 0
      );
      rows.push({
        id: e.name, kind: kindOfEntry(e),
        health: (w.tornOffset === null && !structBad) ? 'ok' : 'damaged',
        frames: w.frames.length, bytes: w.totalBytes,
        tornAt: w.tornOffset, reason: w.tornReason,
        lostPercent: sess.corruption?.lostPercent ?? 0,
        events: sess.stats.events,
        structural: sess.structural,
      });
    } catch (err) {
      rows.push({ id: e.name, kind: kindOfEntry(e), health: 'unreadable', error: err.message });
    }
  }
  return {
    ok: true,
    totals: {
      scanned: rows.length,
      ok: rows.filter((r) => r.health === 'ok').length,
      damaged: rows.filter((r) => r.health === 'damaged').length,
      unreadable: rows.filter((r) => r.health === 'unreadable').length,
      missing: rows.filter((r) => r.health === 'missing-log').length,
      // Structural damage (duplicate seq etc.) is reported separately from zstd
      // framing damage because the two need different repairs.
      structurallyDamaged: rows.filter((r) => r.health === 'damaged' && r.tornAt === null).length,
    },
    damaged: rows.filter((r) => r.health !== 'ok'),
  };
}

// ── archive (DSH's own hide mechanism) ───────────────────────────────────────
function setArchived(ids, archived) {
  const wsState = readWorkspaceState();
  if (!wsState.raw) return { ok: false, error: `workspace store not found at ${WORKSPACE_JSON}` };
  const set = new Set(wsState.archived);
  const changed = [];
  for (const id of ids) {
    const had = set.has(id);
    if (archived && !had) { set.add(id); changed.push({ id, action: 'archived' }); }
    else if (!archived && had) { set.delete(id); changed.push({ id, action: 'unarchived' }); }
    else changed.push({ id, action: had ? 'already-archived' : 'not-archived' });
  }
  if (changed.some((c) => c.action === 'archived' || c.action === 'unarchived')) {
    writeWorkspaceArchived(wsState.raw, set);
  }
  const touched = changed.some((c) => c.action === 'archived' || c.action === 'unarchived');
  return {
    ok: true,
    archivedCount: set.size,
    changed,
    // 这个字段存在的原因（三处源码证据，缺一不可）：
    //  1) DSH 的存储后端 dsh-storage-json 只在「打开单元」时 readFile 一次，注释明写
    //     "The in-memory state is authoritative"，全文没有 watch/stat/reload —— 运行中的
    //     DSH 不会再读这个文件，外部改动对它不可见，必须重启才会重新装载。
    //  2) DSH 的 workspace 接口只暴露 Remote("archiveSession")，且是「只增不改」
    //     （includes(sessionId) 就直接 return）。
    //  3) 全量搜索 @deepseek-ai 下 unarchive / removeArchived / restoreArchived 无任何命中；
    //     客户端界面也只有 "menu.archiveSession"（归档会话）这一项。
    // 结论：**取消归档是 DSH 本身不具备的能力**。我们只能改文件，而改完必须重启 DSH 才生效。
    requiresDshRestart: touched,
    note: touched
      ? '已写入 workspace.json。但运行中的 DSH 把该文件读在内存里、且不监听文件改动，'
        + '其接口也没有「取消归档」这个操作 —— 归档状态要重启 DSH 才会在其界面生效。'
      : null,
  };
}

// ── delete (quarantine) ──────────────────────────────────────────────────────
function deleteSessions(ids, selector = null) {
  const wsState = readWorkspaceState();
  const batch = `batch-${new Date().toISOString().replace(/[:.]/g, '-')}`;
  const batchDir = join(QUARANTINE_ROOT, batch);
  mkdirSync(batchDir, { recursive: true });
  const manifest = [];
  const skipped = [];

  for (const e of walk()) {
    if (!ids.includes(e.name)) continue;
    const rec = describe(e, wsState, { readHeader: false });
    const dest = join(batchDir, `${e.workspace}__${e.name}`);
    try {
      renameSync(e.dir, dest);
      manifest.push({ id: e.name, kind: rec.kind, workspace: e.workspace, from: e.dir, to: dest, bytes: rec.bytes });
    } catch (err) {
      skipped.push({ id: e.name, error: err.message });
    }
  }
  for (const id of ids) if (!manifest.some((m) => m.id === id) && !skipped.some((s) => s.id === id)) skipped.push({ id, error: 'not found' });

  const manifestPath = join(batchDir, 'manifest.json');
  writeFileSync(manifestPath, JSON.stringify({
    batch,
    createdAt: new Date().toISOString(),
    // The selector that produced this batch is recorded because `kind` above is
    // the DIRECTORY class (user/subagent/imported/system), while selectors like
    // `probe` and `empty` are CONTENT-based judgements. Purging by kind alone
    // therefore silently matches nothing for those, which is exactly the bug
    // this field exists to prevent.
    selector,
    items: manifest,
  }, null, 2), 'utf8');
  return { ok: true, batch, selector, manifestPath, removed: manifest, skipped };
}

function restoreBatch(manifestPath) {
  if (!existsSync(manifestPath)) return { ok: false, error: `manifest not found: ${manifestPath}` };
  const m = JSON.parse(readFileSync(manifestPath, 'utf8'));
  const restored = [];
  const failed = [];
  for (const item of m.items) {
    if (!existsSync(item.to)) { failed.push({ id: item.id, error: 'quarantined copy is missing' }); continue; }
    if (existsSync(item.from)) { failed.push({ id: item.id, error: 'destination already exists, refusing to overwrite' }); continue; }
    try { mkdirSync(join(item.from, '..'), { recursive: true }); renameSync(item.to, item.from); restored.push(item.id); }
    catch (err) { failed.push({ id: item.id, error: err.message }); }
  }
  return { ok: true, restored, failed };
}

/**
 * Permanent destruction. Only ever touches quarantined copies.
 *
 * Two targeting modes, because they answer different questions:
 *   --kind <k>    destroy items whose DIRECTORY class is k (user/subagent/imported/system)
 *   --batch <b>   destroy one quarantine batch wholesale, whatever its selector was
 *   --all         destroy everything quarantine holds
 * `--batch` exists because content-based selectors (probe/empty) do not map onto
 * the directory class, so purging "the probes" by kind matches nothing.
 */
function purge({ kind = null, batch = null, all = false } = {}) {
  if (!existsSync(QUARANTINE_ROOT)) return { ok: true, purgedCount: 0, purged: [], note: 'nothing quarantined' };
  const purged = [];
  const batches = [];
  for (const b of readdirSync(QUARANTINE_ROOT, { withFileTypes: true })) {
    if (!b.isDirectory()) continue;
    if (batch && b.name !== batch) continue;
    const batchDir = join(QUARANTINE_ROOT, b.name);
    const mf = join(batchDir, 'manifest.json');
    if (!existsSync(mf)) continue;
    const m = JSON.parse(readFileSync(mf, 'utf8'));
    let touched = 0;
    for (const item of m.items) {
      if (kind && item.kind !== kind) continue;
      if (!existsSync(item.to)) continue;
      rmSync(item.to, { recursive: true, force: true });
      purged.push({ id: item.id, kind: item.kind, bytes: item.bytes });
      touched += 1;
    }
    // An emptied batch leaves its manifest behind as the record of what was
    // destroyed and when; only a fully-purged batch has its directory removed.
    const remaining = m.items.filter((i) => existsSync(i.to)).length;
    if (touched > 0 && remaining === 0) rmSync(batchDir, { recursive: true, force: true });
    batches.push({ batch: b.name, selector: m.selector ?? null, destroyed: touched, remaining });
  }
  return {
    ok: true,
    mode: all ? 'all' : batch ? `batch:${batch}` : `kind:${kind}`,
    purgedCount: purged.length,
    purgedBytes: purged.reduce((a, i) => a + (i.bytes ?? 0), 0),
    batches,
    purged,
  };
}

/**
 * Permanent destruction of LIVE sessions — the irreversible counterpart of
 * `delete`.
 *
 * `delete` moves the directory into quarantine, so it is always recoverable;
 * this verb removes it outright. Keeping them as two verbs rather than one
 * "force" flag makes the irreversible path impossible to reach by accident.
 *
 * Three things are cleaned up together, because leaving any of them behind
 * produces a different bug:
 *   - the session directory itself
 *   - any quarantined copy of the same id (otherwise "delete, then purge"
 *     leaves a contradicting copy that can still be restored)
 *   - the id in DSH's archivedSessionIds (a stale reference to a directory that
 *     no longer exists is exactly what makes DSH's session list look like it
 *     lost entries)
 */
function purgeLive(ids) {
  const wsState = readWorkspaceState();
  const idSet = new Set(ids);
  const destroyed = [];
  const skipped = [];

  for (const e of walk()) {
    if (!idSet.has(e.name)) continue;
    const rec = describe(e, wsState, { readHeader: false });
    try {
      rmSync(e.dir, { recursive: true, force: true });
      destroyed.push({ id: e.name, kind: rec.kind, bytes: rec.bytes, dir: e.dir });
    } catch (err) {
      skipped.push({ id: e.name, error: err.message });
    }
  }
  for (const id of ids) {
    if (!destroyed.some((d) => d.id === id) && !skipped.some((s) => s.id === id)) {
      skipped.push({ id, error: 'not found' });
    }
  }

  const quarantineCopies = [];
  if (existsSync(QUARANTINE_ROOT)) {
    for (const b of readdirSync(QUARANTINE_ROOT, { withFileTypes: true })) {
      if (!b.isDirectory()) continue;
      const batchDir = join(QUARANTINE_ROOT, b.name);
      const mf = join(batchDir, 'manifest.json');
      if (!existsSync(mf)) continue;
      const m = JSON.parse(readFileSync(mf, 'utf8'));
      for (const item of m.items) {
        if (!idSet.has(item.id) || !existsSync(item.to)) continue;
        rmSync(item.to, { recursive: true, force: true });
        quarantineCopies.push({ id: item.id, batch: b.name });
      }
      if (m.items.filter((i) => existsSync(i.to)).length === 0) {
        rmSync(batchDir, { recursive: true, force: true });
      }
    }
  }

  let archiveRefsRemoved = 0;
  if (wsState.raw) {
    const set = new Set(wsState.archived);
    for (const d of destroyed) if (set.delete(d.id)) archiveRefsRemoved += 1;
    if (archiveRefsRemoved > 0) writeWorkspaceArchived(wsState.raw, set);
  }

  return {
    ok: true,
    mode: 'ids',
    purgedCount: destroyed.length,
    purgedBytes: destroyed.reduce((a, i) => a + (i.bytes ?? 0), 0),
    purged: destroyed,
    quarantineCopiesDestroyed: quarantineCopies,
    archiveRefsRemoved,
    skipped,
  };
}

// ── repair ───────────────────────────────────────────────────────────────────
function repair(ids) {
  const out = [];
  for (const e of walk()) {
    if (!ids.includes(e.name)) continue;
    if (!existsSync(e.logPath)) { out.push({ id: e.name, repaired: false, reason: 'no log file' }); continue; }

    // A session can need either (or both) of two different repairs, in a fixed
    // order: first drop any unreadable zstd trailer, then — if the remaining
    // events still carry duplicate/out-of-order seq numbers — renumber them.
    // Renumbering is a full rewrite (it must re-anchor the whole tail), so it is
    // done only when the structural check actually fails.
    const framing = repairSession(e.logPath);
    if (!framing.repaired && framing.reason !== 'log is intact, nothing to repair' && framing.tornOffset === undefined) {
      // repairSession refused and gave a reason; keep it.
      out.push({ id: e.name, repaired: false, reason: framing.reason });
      continue;
    }

    const sess = readSession(e.logPath);
    const structBroken = sess.structural && (
      sess.structural.duplicateSeq.length > 0 || sess.structural.outOfOrderSeq > 0);

    if (structBroken) {
      const seq = renumberSessionSeq(e.logPath);
      out.push({
        id: e.name,
        repaired: seq.repaired,
        reason: seq.reason,
        framing: framing.reason,
        seqRenumbered: seq.changed,
        backup: seq.backup ?? framing.backup ?? null,
      });
    } else {
      out.push({
        id: e.name,
        repaired: framing.repaired,
        reason: framing.reason,
        seqRenumbered: 0,
        backup: framing.backup ?? null,
      });
    }
  }
  for (const id of ids) if (!out.some((o) => o.id === id)) out.push({ id, repaired: false, reason: 'not found' });
  return { ok: true, results: out };
}

// ── dispatch ─────────────────────────────────────────────────────────────────
/** `--select <class>` expands to ids; explicit `--id` values are added on top. */
function resolveTargets() {
  const explicit = optList('--id');
  const selector = opt('--select');
  const ids = selector ? selectIds(selector) : [];
  const all = [...new Set([...ids, ...explicit])];
  return { ids: all, fromSelector: ids.length, explicit: explicit.length, selector };
}

try {
  switch (cmd) {
    case 'list': emit(listSessions()); break;
    case 'select': {
      // Preview only: shows what a selector would hit, so a bulk delete is never
      // performed blind.
      const sel = opt('--select') ?? 'subagent';
      const ids = selectIds(sel);
      emit({ ok: true, selector: sel, count: ids.length, ids });
      break;
    }
    case 'show': {
      const id = opt('--id');
      if (!id) throw new Error('show requires --id');
      emit(showSession(id, { tail: Number(opt('--tail', '0')), userOnly: flag('--user-only') }));
      break;
    }
    case 'scan': emit(scanAll()); break;
    case 'archive':
    case 'unarchive': {
      const t = resolveTargets();
      if (flag('--dry-run')) { emit({ ok: true, dryRun: true, wouldChange: t.ids.length, ...t }); break; }
      emit({ ...setArchived(t.ids, cmd === 'archive'), ...t });
      break;
    }
    case 'delete': {
      const t = resolveTargets();
      if (flag('--dry-run')) { emit({ ok: true, dryRun: true, wouldDelete: t.ids.length, ...t }); break; }
      emit({ ...deleteSessions(t.ids, t.selector), ...t });
      break;
    }
    case 'restore': emit(restoreBatch(opt('--manifest'))); break;
    case 'purge': {
      const batch = opt('--batch');
      const kind = opt('--kind');
      const ids = optList('--id');
      if (ids.length) {
        // Permanent deletion of LIVE sessions. --dry-run first is how the UI
        // states what is about to be destroyed before asking for confirmation.
        if (flag('--dry-run')) {
          const wsState = readWorkspaceState();
          const targets = [];
          for (const e of walk()) {
            if (!ids.includes(e.name)) continue;
            const r = describe(e, wsState, { readHeader: false });
            targets.push({ id: r.id, kind: r.kind, bytes: r.bytes });
          }
          emit({ ok: true, dryRun: true, wouldDestroy: targets.length, targets });
          break;
        }
        emit(purgeLive(ids));
        break;
      }
      if (!batch && !kind && !flag('--all')) {
        emit({ ok: false, error: 'purge needs --id <id>..., --batch <name>, --kind <class>, or --all' });
        break;
      }
      emit(purge({ batch, kind, all: flag('--all') }));
      break;
    }
    case 'repair':
    case 'scan-repair': {
      const t = resolveTargets();
      if (flag('--dry-run')) { emit({ ok: true, dryRun: true, wouldRepair: t.ids.length, ...t }); break; }
      emit({ ...repair(t.ids), ...t });
      break;
    }
    case 'quarantine-list': {
      const out = [];
      if (existsSync(QUARANTINE_ROOT)) {
        for (const b of readdirSync(QUARANTINE_ROOT, { withFileTypes: true })) {
          if (!b.isDirectory()) continue;
          const mf = join(QUARANTINE_ROOT, b.name, 'manifest.json');
          if (!existsSync(mf)) continue;
          const m = JSON.parse(readFileSync(mf, 'utf8'));
          out.push({ batch: b.name, createdAt: m.createdAt, manifest: mf, items: m.items.length, bytes: m.items.reduce((a, i) => a + (i.bytes ?? 0), 0) });
        }
      }
      emit({ ok: true, root: QUARANTINE_ROOT, batches: out });
      break;
    }
    default:
      warn('usage: session-manager.mjs <list|show|scan|select|archive|unarchive|delete|restore|purge|repair|quarantine-list> [options]');
      warn(`  --select ${SELECTORS.join('|')}   --id <id>...   --dry-run`);
      warn('  purge --batch <name> | --kind <user|subagent|imported|system> | --all');
      process.exit(2);
  }
} catch (err) {
  emit({ ok: false, error: err.message });
  process.exit(1);
}
