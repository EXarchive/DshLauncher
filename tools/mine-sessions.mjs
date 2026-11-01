/**
 * mine-sessions.mjs — walk every DSH session log and extract durable experience.
 *
 * A session log already contains everything needed to learn from the past:
 *
 *   user/message       what the user actually asked for
 *   todo/write         the task list the agent committed to
 *   tool/call          which tool was used, with which arguments
 *   tool/result        what the tool returned — including its failures
 *   assistant/message  reasoning and the final answer
 *   turn/end           how the turn finished (completed / truncated / aborted)
 *   session/title      what the session was about
 *
 * This miner turns that into a compact, greppable corpus so lessons can be
 * derived from evidence instead of recollection. It never modifies a session.
 *
 * Output (under <out>/):
 *   session-index.json  one record per session: metadata, counts, health
 *   items.jsonl         one extracted item per line: kind/session/time/text
 *   mine-report.txt     human-readable summary
 *
 * Usage: node tools/mine-sessions.mjs [--out DIR] [--min-text N] [--quiet]
 */

import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, basename } from 'node:path';
import { readSession } from './dsh-session-log.mjs';

const argv = process.argv.slice(2);
const arg = (name, dflt) => {
  const i = argv.indexOf(name);
  return i >= 0 && argv[i + 1] ? argv[i + 1] : dflt;
};
const QUIET = argv.includes('--quiet');
const OUT = arg('--out', join(process.cwd(), '_analysis'));
const MIN_TEXT = Number(arg('--min-text', '40'));

const DSH_HOME = process.env.DSH_HOME || join(homedir(), '.dsh');
const SESSIONS = join(DSH_HOME, 'sessions');

const log = (...a) => { if (!QUIET) console.log(...a); };
const clip = (s, n = 700) => {
  const t = String(s ?? '').replace(/\r/g, '').trim();
  return t.length > n ? t.slice(0, n) + ` …[+${t.length - n} chars]` : t;
};

/** Pull plain text out of an assistant/user content array. */
function contentText(content) {
  if (typeof content === 'string') return content;
  if (!Array.isArray(content)) return '';
  return content
    .filter((c) => c && (c.type === 'text' || c.type === 'reasoning'))
    .map((c) => c.text ?? '')
    .join('\n');
}

/** Pull plain text out of a tool result message. */
function toolResultText(data) {
  const parts = [];
  const walk = (node) => {
    if (!node) return;
    if (typeof node === 'string') { parts.push(node); return; }
    if (Array.isArray(node)) { node.forEach(walk); return; }
    if (typeof node === 'object') {
      if (typeof node.text === 'string') parts.push(node.text);
      if (node.content) walk(node.content);
      if (node.message) walk(node.message);
    }
  };
  walk(data?.message?.content ?? data?.content ?? data);
  return parts.join('\n');
}

// Markers that mean "this is worth remembering as a hazard".
//
// Precision matters more than recall here: a `grep` run that merely FOUND the
// word "error" is a success, and counting it as a failure produces lessons about
// phantom problems. So strong markers must be structurally distinctive, and the
// weak prose markers are only trusted when the message is short enough that the
// marker is the message.
const STRONG_MARKERS = [
  /\[exit code:\s*[1-9]\d*\]/,                 // exit code 0 is a success
  /^\s*Error:/m,
  /^\s*(?:TypeError|ReferenceError|SyntaxError|RangeError|URIError):/m,
  /\bTraceback \(most recent call last\)/,
  /\berror (?:CS|NU|MSB|LNK)\d+/,
  /\b(?:ParserError|MethodInvocationException|ToolArgsError|ToolCallError)\b/,
  /\breturned invalid output\b/,
  /\bENOENT\b/, /\bEPERM\b/, /\bEACCES\b/, /\bEBUSY\b/,
  /\b(?:ERR_[A-Z_]+)\b/,
  /\bsandbox:\s*\S/,
  /\bnetwork:\s*\S/,
  /invalid arguments: missing required/,
  /requires reading ".*" first/,
  /cannot overwrite existing/,
  /only `\w+` is callable directly/,
  /^\s*FAIL\s/m,
];
const WEAK_MARKERS = [/失败/, /报错/, /异常/, /崩溃/, /拒绝/, /权限不足/];

/** A successful lookup that happens to contain a scary word is a success. */
const BENIGN_SHAPES = [/^Found \d+ match/, /^\d+ matches? found/, /^\(no matches\)/];

function looksLikeFailure(text) {
  const head = text.trimStart();
  if (BENIGN_SHAPES.some((re) => re.test(head))) return false;
  if (STRONG_MARKERS.some((re) => re.test(text))) return true;
  // Weak prose markers are common in normal Chinese text, so require brevity:
  // a short message containing them IS the complaint.
  return text.length < 400 && WEAK_MARKERS.some((re) => re.test(text));
}

function* sessionLogs() {
  if (!existsSync(SESSIONS)) return;
  for (const ws of readdirSync(SESSIONS, { withFileTypes: true })) {
    if (!ws.isDirectory()) continue;
    const wsDir = join(SESSIONS, ws.name);
    for (const sess of readdirSync(wsDir, { withFileTypes: true })) {
      if (!sess.isDirectory() || !sess.name.startsWith('session-')) continue;
      const dir = join(wsDir, sess.name);
      const main = join(dir, 'session.jsonl.zstd');
      if (!existsSync(main)) {
        yield { workspace: ws.name, sessionId: sess.name, dir, path: main, missing: true };
        continue;
      }
      yield { workspace: ws.name, sessionId: sess.name, dir, path: main, missing: false };
    }
  }
}

mkdirSync(OUT, { recursive: true });
const itemsPath = join(OUT, 'items.jsonl');
const index = [];
const items = [];

let scanned = 0;
let healthy = 0;
let damaged = 0;
let missing = 0;
let totalEvents = 0;
const toolFailures = new Map();
const toolCalls = new Map();

for (const s of sessionLogs()) {
  scanned += 1;
  if (s.missing) {
    missing += 1;
    index.push({ sessionId: s.sessionId, workspace: s.workspace, health: 'missing-log', path: s.path });
    continue;
  }

  let sess;
  try {
    sess = readSession(s.path);
  } catch (e) {
    index.push({ sessionId: s.sessionId, workspace: s.workspace, health: 'unreadable', error: e.message, path: s.path });
    damaged += 1;
    continue;
  }

  const bytes = statSync(s.path).size;
  totalEvents += sess.stats.events;
  if (sess.ok) healthy += 1; else damaged += 1;

  const rec = {
    sessionId: s.sessionId,
    workspace: s.workspace,
    title: null,
    cwd: sess.cwd,
    createdAt: sess.createdAt,
    createdAtIso: sess.createdAt ? new Date(sess.createdAt).toISOString() : null,
    preset: sess.agentPreset,
    bytes,
    health: sess.ok ? 'ok' : 'damaged',
    corruption: sess.corruption,
    counts: sess.eventCounts,
    userMessages: 0,
    toolCalls: 0,
    toolErrors: 0,
    firstUserText: null,
    path: s.path,
  };

  const titleEvent = sess.events.find((e) => e.type === 'session/title');
  if (titleEvent?.data?.title) rec.title = titleEvent.data.title;

  const push = (kind, e, text) => {
    const t = clip(text);
    if (!t || t.length < MIN_TEXT) return;
    items.push({ kind, sessionId: s.sessionId, workspace: s.workspace, time: e?.time ?? null, date: e?.time ? new Date(e.time).toISOString() : null, text: t });
  };

  const callNames = new Map();
  for (const e of sess.events) {
    if (e.type === 'tool/call') {
      const n = e.data?.name ?? 'unknown';
      callNames.set(e.data?.callId, n);
      toolCalls.set(n, (toolCalls.get(n) ?? 0) + 1);
      rec.toolCalls += 1;
    } else if (e.type === 'user/message') {
      const text = contentText(e.data?.content);
      rec.userMessages += 1;
      if (!rec.firstUserText) rec.firstUserText = clip(text, 200);
      push('user-request', e, text);
    } else if (e.type === 'assistant/message') {
      const text = contentText(e.data?.message?.content);
      // Keep only the non-reasoning part as a conclusion; reasoning is noisy.
      const answer = (e.data?.message?.content ?? [])
        .filter((c) => c?.type === 'text')
        .map((c) => c.text ?? '')
        .join('\n');
      if (answer.trim()) push('assistant-answer', e, answer);
      else if (text.trim()) push('assistant-reasoning', e, text);
    } else if (e.type === 'tool/result') {
      const text = toolResultText(e.data);
      const name = callNames.get(e.data?.message?.source?.callId ?? e.data?.callId) ?? '?';
      if (looksLikeFailure(text)) {
        rec.toolErrors += 1;
        toolFailures.set(name, (toolFailures.get(name) ?? 0) + 1);
        push('tool-failure', e, `[${name}] ${text}`);
      }
    } else if (e.type === 'todo/write') {
      const todos = (e.data?.todos ?? []).map((t) => `${t.status}: ${t.content}`).join('\n');
      push('todo-plan', e, todos);
    } else if (e.type === 'turn/end') {
      const kind = e.data?.reason?.kind;
      if (kind && kind !== 'completed') push('turn-abnormal-end', e, `turn ${e.data?.turn} ended with reason=${kind}`);
    }
  }

  index.push(rec);

  if (scanned % 10 === 0) log(`  … ${scanned} sessions scanned`);
}

writeFileSync(join(OUT, 'session-index.json'), JSON.stringify(index, null, 2), 'utf8');
writeFileSync(itemsPath, items.map((i) => JSON.stringify(i)).join('\n') + '\n', 'utf8');

// Failure BURSTS: a session where the same signature repeats is a stuck retry
// loop, which is a different (and more expensive) problem than a one-off error.
const perSession = new Map();
for (const i of items) {
  if (i.kind !== 'tool-failure') continue;
  const sig = i.text.replace(/\s+/g, ' ').slice(0, 120);
  const key = `${i.sessionId}`;
  if (!perSession.has(key)) perSession.set(key, new Map());
  const m = perSession.get(key);
  m.set(sig, (m.get(sig) ?? 0) + 1);
}
const bursts = [];
for (const [sessionId, m] of perSession) {
  for (const [sig, n] of m) {
    if (n >= 25) bursts.push({ sessionId, repeats: n, signature: sig });
  }
}
bursts.sort((a, b) => b.repeats - a.repeats);

const kindCounts = {};
for (const i of items) kindCounts[i.kind] = (kindCounts[i.kind] ?? 0) + 1;

const top = (m, n = 15) => [...m.entries()].sort((a, b) => b[1] - a[1]).slice(0, n)
  .map(([k, v]) => `    ${String(v).padStart(6)}  ${k}`).join('\n');

const byWorkspace = {};
for (const r of index) byWorkspace[r.workspace] = (byWorkspace[r.workspace] ?? 0) + 1;

const report = [
  `DSH session corpus`,
  `  sessions scanned : ${scanned}`,
  `  healthy          : ${healthy}`,
  `  damaged          : ${damaged}`,
  `  log missing      : ${missing}`,
  `  total events     : ${totalEvents}`,
  `  items extracted  : ${items.length}`,
  '',
  `  workspaces (${Object.keys(byWorkspace).length}):`,
  top(new Map(Object.entries(byWorkspace)), 20),
  '',
  '  items by kind:',
  top(new Map(Object.entries(kindCounts))),
  '',
  '  most-used tools:',
  top(toolCalls, 20),
  '',
  '  tools that FAILED most often:',
  top(toolFailures, 20),
  '',
  '  STUCK RETRY LOOPS (>=25 identical failures inside one session):',
  ...(bursts.length === 0
    ? ['    (none)']
    : bursts.slice(0, 15).map((b) => `    ${String(b.repeats).padStart(5)}x  ${b.sessionId}\n            ${b.signature.slice(0, 110)}`)),
  '',
  `  damaged sessions:`,
  ...index.filter((r) => r.health !== 'ok').map((r) => `    ${r.health.padEnd(12)} ${r.sessionId}  ${r.corruption ? `lost ${r.corruption.lostPercent}% (${r.corruption.reason})` : ''}`),
  '',
].join('\n');

writeFileSync(join(OUT, 'mine-report.txt'), report, 'utf8');
log(report);
log(`written: ${join(OUT, 'session-index.json')}, ${itemsPath}, ${join(OUT, 'mine-report.txt')}`);
