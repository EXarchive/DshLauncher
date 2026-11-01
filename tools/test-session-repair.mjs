#!/usr/bin/env node
/**
 * test-session-repair.mjs — corruption matrix and repair proof for DSH session logs.
 *
 * WHAT THIS PROVES
 * ----------------
 * A DSH session log is a concatenated-frame zstd container ([frame0][frame1]...) in
 * which frame 0 carries the session header. Corrupting that container in every shape
 * a crash can produce must leave `readFrames` / `readSession` / `repairSession`
 * (tools/dsh-session-log.mjs) behaving the same way every time:
 *
 *   - reading NEVER throws on corruption, it describes it;
 *   - repair happens ONLY when a non-empty intact prefix survives;
 *   - the truncation point repair picks lands exactly on a frame boundary — proven
 *     by re-walking the repaired file and requiring the walk to consume it end to
 *     end with zero remainder (sum of frame sizes === file size);
 *   - the pre-repair bytes survive in a backup that is byte-identical to the original;
 *   - repair is idempotent: a second run is a no-op that touches nothing.
 *
 * HOW FIXTURES ARE BUILT
 * ----------------------
 * Every fixture is a COPY of a REAL log from %DSH_HOME%\sessions that is then damaged
 * in a temp directory. Nothing is hand-written (the only synthetic bytes are the
 * trailing junk of case 8, which is what that case is defined to be), and the real
 * log itself is only ever read — the suite re-hashes it at the end and fails if it
 * changed. The temp directory is removed even when a case fails.
 *
 * Frame boundaries are always computed by parsing (frameSize), never by scanning for
 * the zstd magic: 0x28B52FFD also occurs inside compressed payloads.
 *
 * Usage: node tools\test-session-repair.mjs      (exit 0 = every case passed)
 */

import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { join } from 'node:path';
import { zstdDecompressSync } from 'node:zlib';

import { MAGIC_LE, frameSize, readFrames, readSession, repairSession, sessionLogPath } from './dsh-session-log.mjs';

const DSH_HOME = process.env.DSH_HOME || join(homedir(), '.dsh');
const SESSIONS_ROOT = join(DSH_HOME, 'sessions');
const BAD_MAGIC = [0xde, 0xad, 0xbe, 0xef];

// ── report harness ───────────────────────────────────────────────────────────
const counts = { pass: 0, fail: 0, info: 0 };
const observations = [];
let currentCase = 'fixtures';

function check(label, ok, detail = '') {
  if (ok) counts.pass += 1;
  else counts.fail += 1;
  console.log(`  ${ok ? 'PASS' : 'FAIL'}  [${label}]${detail ? ` ${detail}` : ''}`);
  return ok;
}
function info(msg) {
  counts.info += 1;
  console.log(`  INFO  ${msg}`);
}
function caseHeader(n, title) {
  currentCase = `${n}`;
  console.log(`\nCASE ${n}  ${title}`);
}
function observe(fields) {
  observations.push({ case: currentCase, ...fields });
  console.log(`  obs   ${Object.entries(fields).map(([k, v]) => `${k}=${v}`).join(' ')}`);
}

// ── small utilities ──────────────────────────────────────────────────────────
const sha = (buf) => createHash('sha256').update(buf).digest('hex');
const hashOf = (p) => sha(readFileSync(p));
function snapshot(p) {
  const s = statSync(p);
  return { size: s.size, mtimeMs: s.mtimeMs, hash: hashOf(p) };
}
const fmtBytes = (n) => `${n}B`;

let DIR = null;
const fixture = (name, buf) => {
  const p = join(DIR, name);
  writeFileSync(p, buf);
  return p;
};
const backupCount = (prefix) => readdirSync(DIR).filter((f) => f.startsWith(prefix) && f.includes('.corrupt-')).length;

/** Every frame must start at the magic and the walk must consume the file exactly. */
function boundaryReport(p) {
  const w = readFrames(p);
  const buf = readFileSync(p);
  const sum = w.frames.reduce((a, f) => a + f.compressed, 0);
  const size = statSync(p).size;
  const contiguous = w.frames.every((f, i) => (i === 0 ? f.offset === 0 : f.offset === w.frames[i - 1].offset + w.frames[i - 1].compressed));
  const atMagic = w.frames.every((f) => buf.subarray(f.offset, f.offset + 4).equals(MAGIC_LE));
  return { w, sum, size, contiguous, atMagic, clean: w.tornOffset === null && sum === size && contiguous };
}

/** The strongest statement available after a repair: boundary-clean AND exact prefix of the source. */
function checkRepairBoundary(caseId, p, expectedSize, prefixBuf) {
  const b = boundaryReport(p);
  check(`${caseId}x1`, b.w.tornOffset === null, `re-walk of repaired file: tornOffset=${b.w.tornOffset}`);
  check(`${caseId}x2`, b.sum === b.size && b.sum === expectedSize,
    `walk consumed ${b.sum} of ${b.size} bytes end to end (expected ${expectedSize}); frames=${b.w.frames.length}`);
  check(`${caseId}x3`, b.contiguous && b.atMagic, `every frame starts at the magic and is contiguous`);
  check(`${caseId}x4`, sha(readFileSync(p)) === sha(prefixBuf),
    `repaired bytes are byte-identical to the ${fmtBytes(prefixBuf.length)} intact prefix (nothing invented)`);
  return b;
}

/** readSession must describe, never throw — for every damaged shape. */
function safeReadSession(p) {
  try {
    return { ok: true, session: readSession(p), threw: null };
  } catch (e) {
    return { ok: false, session: null, threw: e.message };
  }
}

/** The rule under test: repair only when a non-empty intact prefix exists. */
function checkRepairDecision(caseId, walk, r, fileSize) {
  const prefix = walk.tornOffset === null ? fileSize : walk.tornOffset;
  const shouldRepair = prefix > 0 && walk.tornOffset !== null;
  check(`${caseId}c1`, r.repaired === shouldRepair,
    `repaired=${r.repaired} expected=${shouldRepair} (intact prefix=${prefix}B of ${fileSize}B)`);
  check(`${caseId}c2`, r.before === fileSize, `before=${r.before} file size=${fileSize}`);
  check(`${caseId}c3`, r.before - r.after === r.droppedBytes || r.droppedBytes === undefined,
    `before=${r.before} after=${r.after} dropped=${r.droppedBytes} (before-after=${r.before - r.after})`);
  if (r.repaired) {
    check(`${caseId}c4`, r.after === prefix, `after=${r.after} equals the intact prefix boundary ${prefix}`);
  } else {
    check(`${caseId}c5`, typeof r.reason === 'string' && r.reason.length > 0,
      `refused/no-op with a non-empty reason: "${r.reason}"`);
  }
  return shouldRepair;
}

/** Second run must be a pure no-op: no repair, no second backup, byte-identical file. */
function checkIdempotent(caseId, p, namePrefix) {
  const before = snapshot(p);
  const backupsBefore = backupCount(namePrefix);
  const r2 = repairSession(p);
  const after = snapshot(p);
  check(`${caseId}f1`, r2.repaired === false, `second run repaired=${r2.repaired} reason="${r2.reason}"`);
  check(`${caseId}f2`, r2.reason.length > 0, `second run still reports a reason`);
  check(`${caseId}f3`, after.hash === before.hash && after.size === before.size && after.mtimeMs === before.mtimeMs,
    `file unchanged: hash ${before.hash.slice(0, 12)}… size ${before.size} mtime ${before.mtimeMs}`);
  check(`${caseId}f4`, backupCount(namePrefix) === backupsBefore, `no extra backup created (${backupsBefore} before, ${backupCount(namePrefix)} after)`);
}

function checkBackup(caseId, r, preRepairHash) {
  check(`${caseId}d1`, typeof r.backup === 'string' && existsSync(r.backup), `backup file exists: ${r.backup}`);
  const ok = r.backup && existsSync(r.backup) && hashOf(r.backup) === preRepairHash;
  check(`${caseId}d2`, ok, `backup sha256 identical to the pre-repair file (${preRepairHash.slice(0, 16)}…)`);
  check(`${caseId}d3`, existsSync(r.backup) && statSync(r.backup).size === r.before, `backup size ${r.before}B equals before`);
}

// ── corpus / fixture source ──────────────────────────────────────────────────
function candidateLogs() {
  const out = [];
  if (!existsSync(SESSIONS_ROOT)) return out;
  for (const ws of readdirSync(SESSIONS_ROOT, { withFileTypes: true })) {
    if (!ws.isDirectory()) continue;
    const wsDir = join(SESSIONS_ROOT, ws.name);
    for (const d of readdirSync(wsDir, { withFileTypes: true })) {
      if (!d.isDirectory()) continue;
      // sessionLogPath() is the exported layout helper; use it so a drift between
      // the helper and the real tree is caught here rather than by the caller.
      const p = sessionLogPath(SESSIONS_ROOT, ws.name, d.name);
      if (existsSync(p)) out.push({ path: p, workspace: ws.name, session: d.name, bytes: statSync(p).size });
    }
  }
  return out;
}

function pickFixtureSource() {
  const inWindow = candidateLogs().filter((c) => c.bytes >= 4096 && c.bytes <= 2 * 1024 * 1024).sort((a, b) => a.bytes - b.bytes);
  const all = candidateLogs().filter((c) => c.bytes > 0).sort((a, b) => a.bytes - b.bytes);
  const reject = [];
  for (const c of [...inWindow, ...all]) {
    let walk;
    try {
      walk = readFrames(c.path);
    } catch (e) {
      reject.push(`${c.session}: unreadable (${e.message})`);
      continue;
    }
    if (walk.tornOffset !== null) { reject.push(`${c.session}: damaged at ${walk.tornOffset}`); continue; }
    if (walk.frames.length < 8) { reject.push(`${c.session}: only ${walk.frames.length} frames`); continue; }
    if (walk.frames[0].firstType !== 'session') { reject.push(`${c.session}: frame 0 is not a session header`); continue; }
    return { ...c, walk, rejected: reject };
  }
  return { rejected: reject };
}

// ── case 3 helper: damage that keeps framing intact but breaks decoding ──────
/**
 * Flip bytes in the MIDDLE of a frame body so the frame stays structurally complete
 * (its declared size is unchanged, its magic is intact) but can no longer decode.
 * Searching outward from the body centre keeps the chosen offset deterministic.
 */
function makeMidBodyDamage(good, walk) {
  const mid = Math.max(1, Math.floor(walk.frames.length / 2));
  const order = [mid];
  for (let d = 1; d < walk.frames.length; d += 1) {
    if (mid + d < walk.frames.length) order.push(mid + d);
    if (mid - d >= 1) order.push(mid - d);
  }
  for (const idx of order) {
    const f = walk.frames[idx];
    if (f.compressed < 32) continue;
    const centre = Math.floor(f.compressed / 2);
    for (let d = 0; d < f.compressed; d += 8) {
      for (const at of [centre - d, centre + d]) {
        if (at < 8 || at + 8 > f.compressed - 8) continue;
        const cand = Buffer.from(good);
        for (let i = 0; i < 8; i += 1) cand[f.offset + at + i] ^= 0xa5;
        if (!cand.subarray(f.offset, f.offset + f.compressed).equals(good.subarray(f.offset, f.offset + f.compressed))) {
          let sizeIntact = false;
          try { sizeIntact = frameSize(cand, f.offset).total === f.compressed; } catch { sizeIntact = false; }
          if (!sizeIntact) continue;
          let decodes = true;
          try { zstdDecompressSync(cand.subarray(f.offset, f.offset + f.compressed)); } catch { decodes = false; }
          if (!decodes) return { buf: cand, frame: f, frameIndex: idx, bodyOffset: at };
        }
      }
    }
  }
  return null;
}

/** Deterministic non-frame junk: never starts with the zstd magic, so the walk must reject it immediately. */
function junkBytes(n) {
  const b = Buffer.alloc(n);
  let x = 0x12345678;
  for (let i = 0; i < n; i += 1) {
    x = (x * 1103515245 + 12345) & 0x7fffffff;
    b[i] = (x >> 16) & 0xff;
  }
  b[0] = 0x00;
  return b;
}

// ── main ─────────────────────────────────────────────────────────────────────
console.log('DSH session-log corruption matrix — tools\\dsh-session-log.mjs');
console.log(`sessions root : ${SESSIONS_ROOT}`);

DIR = mkdtempSync(join(tmpdir(), 'dsh-session-repair-'));
console.log(`fixture dir   : ${DIR}`);

let sourceSnapshot = null;
try {
  // ── fixture source ────────────────────────────────────────────────────────
  const source = pickFixtureSource();
  if (!source.path) {
    check('fixture-source', false, `no intact real log with >= 8 frames under ${SESSIONS_ROOT} (${source.rejected.slice(0, 5).join('; ')})`);
    throw new Error('cannot build the matrix without a real fixture source');
  }
  check('fixture-source', true, `${source.path} (${fmtBytes(source.bytes)}, ${source.walk.frames.length} frames)`);
  sourceSnapshot = snapshot(source.path);

  const good = readFileSync(source.path);
  const goodWalk = source.walk;
  const goodSession = readSession(source.path);
  const goodSum = goodWalk.frames.reduce((a, f) => a + f.compressed, 0);

  check('source-layout', sessionLogPath(SESSIONS_ROOT, source.workspace, source.session) === source.path,
    'sessionLogPath() resolves the same file the walk found');
  check('source-intact', goodWalk.tornOffset === null, `tornOffset=${goodWalk.tornOffset} tornReason=${goodWalk.tornReason}`);
  check('source-frameSize-agrees', goodWalk.frames.every((f) => frameSize(good, f.offset).total === f.compressed),
    'frameSize() equals the walked size for every frame');
  check('source-walk-consumes-all', goodSum === good.length, `sum(frame sizes)=${goodSum} file=${good.length}`);
  check('source-session', goodSession.ok === true && goodSession.id !== null,
    `readSession ok=${goodSession.ok} id=${goodSession.id} events=${goodSession.events.length}`);

  // ── CASE 1 — intact ───────────────────────────────────────────────────────
  caseHeader(1, 'intact — no corruption; repair must be a no-op and must not touch the file');
  {
    const p = fixture('intact.zstd', good);
    const before = snapshot(p);

    const w = readFrames(p);
    check('1a1', w.tornOffset === null && w.tornReason === null, `tornOffset=${w.tornOffset} tornReason=${w.tornReason}`);
    check('1a2', w.frames.length === goodWalk.frames.length && w.totalBytes === good.length, `frames=${w.frames.length} bytes=${w.totalBytes}`);

    const rs = safeReadSession(p);
    check('1b1', rs.threw === null, `readSession did not throw${rs.threw ? `: ${rs.threw}` : ''}`);
    check('1b2', rs.session?.ok === true && rs.session.events.length === goodSession.events.length,
      `ok=${rs.session?.ok} events=${rs.session?.events.length} (source ${goodSession.events.length})`);
    check('1b3', rs.session?.id === goodSession.id && rs.session?.corruption === null, `corruption=${JSON.stringify(rs.session?.corruption)}`);

    const r = repairSession(p);
    checkRepairDecision('1', w, r, good.length);
    check('1d1', r.backup === null, `backup=${r.backup}`);

    const after = snapshot(p);
    check('1e1', after.hash === before.hash && after.size === before.size && after.mtimeMs === before.mtimeMs,
      `file untouched: hash ${after.hash.slice(0, 12)}… size ${after.size} mtime ${after.mtimeMs}`);
    checkRepairBoundary('1', p, good.length, good);
    checkIdempotent('1', p, 'intact.zstd');

    observe({ tornOffset: w.tornOffset, tornReason: w.tornReason, repaired: r.repaired, reason: `"${r.reason}"`, backup: r.backup });
  }

  // ── CASE 2 — torn tail ────────────────────────────────────────────────────
  caseHeader(2, 'torn tail — truncated inside the last frame (mid-write crash)');
  {
    const last = goodWalk.frames[goodWalk.frames.length - 1];
    const cutAt = last.offset + Math.max(1, Math.floor(last.compressed / 2));
    check('2f0', cutAt > last.offset && cutAt < last.offset + last.compressed,
      `cut at ${cutAt} is provably inside the last frame [${last.offset}, ${last.offset + last.compressed})`);

    const p = fixture('torn-tail.zstd', good.subarray(0, cutAt));
    const pre = snapshot(p);

    const w = readFrames(p);
    check('2a1', w.tornOffset === last.offset, `tornOffset=${w.tornOffset} last frame offset=${last.offset}`);
    check('2a2', /^(truncated frame|truncated block header)/.test(w.tornReason ?? ''), `tornReason="${w.tornReason}"`);
    check('2a3', w.frames.length === goodWalk.frames.length - 1, `good frames=${w.frames.length}`);

    const rs = safeReadSession(p);
    check('2b1', rs.threw === null, `readSession did not throw${rs.threw ? `: ${rs.threw}` : ''}`);
    check('2b2', rs.session.events.length > 0 && rs.session.id === goodSession.id && rs.session.stats.frames === w.frames.length,
      `kept ${rs.session.events.length} events, ${rs.session.stats.frames} frames, header id intact`);
    check('2b3', rs.session.corruption?.atByte === last.offset && rs.session.corruption?.repairable === true,
      `corruption=${JSON.stringify(rs.session.corruption)}`);

    const r = repairSession(p);
    checkRepairDecision('2', w, r, cutAt);
    check('2c6', r.repaired === true && r.after === last.offset && r.droppedBytes === cutAt - last.offset,
      `repaired to ${r.after} (last complete frame boundary), dropped ${r.droppedBytes}B`);
    checkBackup('2', r, pre.hash);
    check(`${'2'}e1`, readFrames(p).tornOffset === null, 'after repair readFrames reports tornOffset=null');
    checkRepairBoundary('2', p, last.offset, good.subarray(0, last.offset));
    checkIdempotent('2', p, 'torn-tail.zstd');

    observe({ tornOffset: w.tornOffset, tornReason: `"${w.tornReason}"`, before: r.before, after: r.after, dropped: r.droppedBytes, repaired: r.repaired });
  }

  // ── CASE 3 — mid-file damage (structurally complete frame that will not decode) ──
  caseHeader(3, 'mid-file damage — body bytes flipped inside a middle frame');
  {
    const dmg = makeMidBodyDamage(good, goodWalk);
    if (!dmg) throw new Error('could not find a frame whose body damage keeps the framing intact');
    const p = fixture('mid-flip.zstd', dmg.buf);
    const pre = snapshot(p);

    check('3f0', frameSize(dmg.buf, dmg.frame.offset).total === dmg.frame.compressed,
      `frame #${dmg.frameIndex} at ${dmg.frame.offset} stays structurally complete (declared ${dmg.frame.compressed}B), body byte ${dmg.bodyOffset} flipped`);
    check('3f1', dmg.buf.subarray(dmg.frame.offset, dmg.frame.offset + 4).equals(MAGIC_LE), 'the damaged frame keeps its zstd magic');

    const w = readFrames(p);
    check('3a1', w.tornOffset === dmg.frame.offset, `tornOffset=${w.tornOffset} damaged frame offset=${dmg.frame.offset}`);
    check('3a2', /^frame does not decompress: /.test(w.tornReason ?? ''), `tornReason="${w.tornReason}"`);
    check('3a3', w.frames.length === dmg.frameIndex, `frames read before the break=${w.frames.length}`);

    const rs = safeReadSession(p);
    check('3b1', rs.threw === null, `readSession did not throw${rs.threw ? `: ${rs.threw}` : ''}`);
    check('3b2', rs.session.events.length > 0 && rs.session.id === goodSession.id, `kept ${rs.session.events.length} events, header id intact`);
    check('3b3', rs.session.corruption?.atByte === dmg.frame.offset, `corruption.atByte=${rs.session.corruption?.atByte}`);

    const r = repairSession(p);
    checkRepairDecision('3', w, r, good.length);
    check('3c6', r.repaired === true && r.after === dmg.frame.offset && r.droppedBytes === good.length - dmg.frame.offset,
      `repaired to ${r.after}, dropped ${r.droppedBytes}B`);
    checkBackup('3', r, pre.hash);
    check('3e1', readFrames(p).tornOffset === null, 'after repair readFrames reports tornOffset=null');
    checkRepairBoundary('3', p, dmg.frame.offset, good.subarray(0, dmg.frame.offset));
    checkIdempotent('3', p, 'mid-flip.zstd');

    observe({ tornOffset: w.tornOffset, tornReason: `"${w.tornReason}"`, before: r.before, after: r.after, dropped: r.droppedBytes, repaired: r.repaired });
  }

  // ── CASE 4 — bad magic on a middle frame ──────────────────────────────────
  caseHeader(4, 'bad magic mid-file — 4 magic bytes of a middle frame overwritten');
  {
    const idx = Math.max(1, Math.floor(goodWalk.frames.length / 2));
    const target = goodWalk.frames[idx];
    const buf = Buffer.from(good);
    buf.set(BAD_MAGIC, target.offset);
    const p = fixture('bad-magic.zstd', buf);
    const pre = snapshot(p);

    check('4f0', !buf.subarray(target.offset, target.offset + 4).equals(MAGIC_LE), `frame #${idx} magic replaced at ${target.offset}`);
    check('4f1', buf.subarray(target.offset, target.offset + 4).equals(Buffer.from(BAD_MAGIC)) && idx >= 1,
      `junk magic written at ${target.offset} on frame #${idx} of ${goodWalk.frames.length}, so an intact prefix exists`);

    const w = readFrames(p);
    check('4a1', w.tornOffset === target.offset, `tornOffset=${w.tornOffset} damaged frame offset=${target.offset}`);
    check('4a2', w.tornReason === `invalid frame magic at byte ${target.offset}`, `tornReason="${w.tornReason}" (exact)`);
    check('4a3', w.frames.length === idx, `frames read before the break=${w.frames.length}`);

    const rs = safeReadSession(p);
    check('4b1', rs.threw === null, `readSession did not throw${rs.threw ? `: ${rs.threw}` : ''}`);
    check('4b2', rs.session.events.length > 0 && rs.session.id === goodSession.id, `kept ${rs.session.events.length} events, header id intact`);
    check('4b3', rs.session.corruption?.atByte === target.offset && rs.session.corruption?.repairable === true,
      `corruption=${JSON.stringify(rs.session.corruption)}`);

    const r = repairSession(p);
    checkRepairDecision('4', w, r, good.length);
    check('4c6', r.repaired === true && r.after === target.offset && r.droppedBytes === good.length - target.offset,
      `repaired to ${r.after}, dropped ${r.droppedBytes}B`);
    checkBackup('4', r, pre.hash);
    check('4e1', readFrames(p).tornOffset === null, 'after repair readFrames reports tornOffset=null');
    checkRepairBoundary('4', p, target.offset, good.subarray(0, target.offset));
    checkIdempotent('4', p, 'bad-magic.zstd');

    observe({ tornOffset: w.tornOffset, tornReason: `"${w.tornReason}"`, before: r.before, after: r.after, dropped: r.droppedBytes, repaired: r.repaired });
  }

  // ── CASE 5 — zero-length ──────────────────────────────────────────────────
  caseHeader(5, 'zero-length — 0 bytes: no frame 0, no session header, nothing to repair');
  {
    const p = fixture('empty.zstd', good.subarray(0, 0));
    check('5f0', statSync(p).size === 0, 'fixture is 0 bytes');

    const w = readFrames(p);
    check('5a1', w.frames.length === 0 && w.totalBytes === 0, `frames=${w.frames.length} totalBytes=${w.totalBytes}`);
    check('5a2', w.tornOffset === 0 && typeof w.tornReason === 'string' && w.tornReason.length > 0,
      `tornOffset=${w.tornOffset} tornReason="${w.tornReason}"`);

    const rs = safeReadSession(p);
    check('5b1', rs.threw === null, `readSession did not throw${rs.threw ? `: ${rs.threw}` : ''}`);
    check('5b2', rs.session.ok === false && rs.session.events.length === 0 && rs.session.id === null,
      `ok=${rs.session.ok} events=${rs.session.events.length} id=${rs.session.id}`);
    check('5b3', rs.session.corruption?.atByte === 0 && rs.session.corruption?.repairable === false,
      `corruption=${JSON.stringify(rs.session.corruption)}`);

    const before = snapshot(p);
    const r = repairSession(p);
    checkRepairDecision('5', w, r, 0);
    check('5c6', r.repaired === false && /no intact prefix to keep/.test(r.reason), `refused: "${r.reason}"`);
    check('5d1', r.backup === null, 'no backup was created');
    const after = snapshot(p);
    check('5e1', after.hash === before.hash && after.size === 0 && after.mtimeMs === before.mtimeMs, 'file left exactly as it was (0 bytes)');
    checkIdempotent('5', p, 'empty.zstd');

    observe({ tornOffset: w.tornOffset, tornReason: `"${w.tornReason}"`, repaired: r.repaired, reason: `"${r.reason}"`, backup: r.backup });
  }

  // ── CASE 6 — header-only ──────────────────────────────────────────────────
  caseHeader(6, 'header-only — frame 0 (the session header) and nothing else');
  {
    const onlyHeader = good.subarray(0, goodWalk.frames[0].compressed);
    const p = fixture('header-only.zstd', onlyHeader);
    const before = snapshot(p);

    const w = readFrames(p);
    check('6a1', w.tornOffset === null && w.tornReason === null, `tornOffset=${w.tornOffset} tornReason=${w.tornReason}`);
    check('6a2', w.frames.length === 1 && w.frames[0].firstType === 'session' && w.frames[0].offset === 0,
      `frames=${w.frames.length} firstType=${w.frames[0]?.firstType}`);

    const rs = safeReadSession(p);
    check('6b1', rs.threw === null, `readSession did not throw${rs.threw ? `: ${rs.threw}` : ''}`);
    check('6b2', rs.session.ok === true && rs.session.id === goodSession.id && rs.session.corruption === null,
      `ok=${rs.session.ok} id=${rs.session.id} events=${rs.session.events.length}`);

    const r = repairSession(p);
    checkRepairDecision('6', w, r, onlyHeader.length);
    check('6g1', typeof r.reason === 'string' && r.reason.length > 0, `reason="${r.reason}"`);
    check('6d1', r.backup === null, 'no backup was created');
    const after = snapshot(p);
    check('6e1', after.hash === before.hash && after.mtimeMs === before.mtimeMs, 'file untouched');
    checkRepairBoundary('6', p, onlyHeader.length, onlyHeader);
    checkIdempotent('6', p, 'header-only.zstd');

    observe({ tornOffset: w.tornOffset, tornReason: w.tornReason, repaired: r.repaired, reason: `"${r.reason}"`, backup: r.backup });
  }

  // ── CASE 7 — truncated header (inside frame 0 itself) ─────────────────────
  caseHeader(7, 'truncated header — cut inside the first frame\u2019s own header (3 bytes)');
  {
    const p = fixture('cut-header.zstd', good.subarray(0, 3));
    const before = snapshot(p);

    const w = readFrames(p);
    check('7a1', w.tornOffset === 0, `tornOffset=${w.tornOffset} (the very first byte position)`);
    check('7a2', w.tornReason === 'truncated frame header at byte 0', `tornReason="${w.tornReason}" (exact)`);
    check('7a3', w.frames.length === 0 && w.goodBytes === 0, `frames=${w.frames.length} goodBytes=${w.goodBytes}`);

    const rs = safeReadSession(p);
    check('7b1', rs.threw === null, `readSession did not throw${rs.threw ? `: ${rs.threw}` : ''}`);
    check('7b2', rs.session.ok === false && rs.session.events.length === 0 && rs.session.id === null,
      `ok=${rs.session.ok} events=${rs.session.events.length} id=${rs.session.id}`);

    const r = repairSession(p);
    checkRepairDecision('7', w, r, 3);
    check('7g1', r.repaired === false && /no intact prefix to keep/.test(r.reason), `refused: "${r.reason}"`);
    check('7d1', r.backup === null, 'no backup was created');
    const after = snapshot(p);
    check('7e1', after.hash === before.hash && after.size === 3 && after.mtimeMs === before.mtimeMs, 'file untouched (3 bytes)');
    checkIdempotent('7', p, 'cut-header.zstd');

    observe({ tornOffset: w.tornOffset, tornReason: `"${w.tornReason}"`, repaired: r.repaired, reason: `"${r.reason}"`, backup: r.backup });
  }

  // ── CASE 8 — trailing garbage ─────────────────────────────────────────────
  caseHeader(8, 'trailing garbage — a valid log with non-frame bytes appended');
  {
    const junk = junkBytes(37);
    const p = fixture('trailing.zstd', Buffer.concat([good, junk]));
    const pre = snapshot(p);

    const w = readFrames(p);
    check('8a1', w.tornOffset === good.length, `tornOffset=${w.tornOffset} equals the end of the last real frame (${good.length})`);
    check('8a2', w.tornReason === `invalid frame magic at byte ${good.length}`, `tornReason="${w.tornReason}" (exact)`);
    check('8a3', w.frames.length === goodWalk.frames.length && w.goodBytes === good.length, `frames=${w.frames.length} goodBytes=${w.goodBytes}`);

    const rs = safeReadSession(p);
    check('8b1', rs.threw === null, `readSession did not throw${rs.threw ? `: ${rs.threw}` : ''}`);
    check('8b2', rs.session.events.length === goodSession.events.length && rs.session.id === goodSession.id,
      `kept every event: ${rs.session.events.length} (source ${goodSession.events.length})`);
    check('8b3', rs.session.corruption?.lostBytes === junk.length, `lostBytes=${rs.session.corruption?.lostBytes} === junk ${junk.length}B`);

    const r = repairSession(p);
    checkRepairDecision('8', w, r, good.length + junk.length);
    check('8c6', r.repaired === true && r.after === good.length && r.droppedBytes === junk.length,
      `repaired to ${r.after}, dropped ${r.droppedBytes}B of junk`);
    checkBackup('8', r, pre.hash);
    check('8e1', readFrames(p).tornOffset === null, 'after repair readFrames reports tornOffset=null');
    checkRepairBoundary('8', p, good.length, good);
    check('8x5', hashOf(p) === sha(good), 'repaired file is byte-identical to the pristine source log');
    checkIdempotent('8', p, 'trailing.zstd');

    observe({ tornOffset: w.tornOffset, tornReason: `"${w.tornReason}"`, before: r.before, after: r.after, dropped: r.droppedBytes, repaired: r.repaired });
  }

  // ── cross-check: a real, large log ────────────────────────────────────────
  console.log('\nCROSS-CHECK  large real logs (frame walking, not magic scanning)');
  {
    const big = candidateLogs().filter((c) => c.bytes > 1024 * 1024 && c.bytes <= 32 * 1024 * 1024).sort((a, b) => b.bytes - a.bytes).slice(0, 3);
    const rows = [];
    for (const c of big) {
      const buf = readFileSync(c.path);
      let occ = 0;
      for (let i = buf.indexOf(MAGIC_LE); i >= 0; i = buf.indexOf(MAGIC_LE, i + 1)) occ += 1;
      const w = readFrames(c.path);
      if (w.tornOffset !== null) { info(`${c.session}: damaged at ${w.tornOffset}, skipped`); continue; }
      const sum = w.frames.reduce((a, f) => a + f.compressed, 0);
      rows.push({ ...c, occ, frames: w.frames.length, scattered: occ - w.frames.length, sum, clean: sum === c.bytes });
    }
    if (rows.length === 0) {
      info('no intact real log above 1MB available; the large-log cross-check was skipped');
    } else {
      const best = rows.reduce((a, b) => (b.scattered > a.scattered ? b : a));
      for (const r of rows) {
        info(`${(r.bytes / 1048576).toFixed(2)}MB ${r.session}: frames=${r.frames} magic occurrences=${r.occ} magic-inside-payloads=${r.scattered}`);
      }
      check('bigx1', rows.every((r) => r.clean), `every large log\'s frames consume it end to end (${best.frames} frames in ${(best.bytes / 1048576).toFixed(2)}MB)`);
      check('bigx2', readFrames(best.path).frames.every((f, i, a) => (i === 0 ? f.offset === 0 : f.offset === a[i - 1].offset + a[i - 1].compressed)),
        'large-log frames are contiguous from byte 0 to EOF');
      if (best.scattered > 0) {
        check('bigx3', true,
          `magic scanning would report ${best.occ} frame starts where there are ${best.frames} frames (${best.scattered} magics live inside payloads) — frameSize parsing is the only correct rule`);
      } else {
        info('in the largest logs sampled here every magic is a frame start, so this corpus cannot demonstrate the trap on a large log — the walk is still the correct rule (see the case-4 fixture: one overwritten magic makes a scanner lose a boundary entirely)');
      }
    }
  }

  // ── the real log must be untouched ────────────────────────────────────────
  console.log('\nCROSS-CHECK  fixtures never mutated their source');
  {
    const now = snapshot(source.path);
    check('src1', now.hash === sourceSnapshot.hash && now.size === sourceSnapshot.size && now.mtimeMs === sourceSnapshot.mtimeMs,
      `source log unchanged (sha256 ${now.hash.slice(0, 16)}…, ${now.size}B, mtime ${now.mtimeMs})`);
  }
} catch (e) {
  check('suite-completed', false, `${e.message}`);
  console.log(e.stack);
} finally {
  try {
    rmSync(DIR, { recursive: true, force: true });
    console.log(`\nfixture dir removed: ${DIR} (exists=${existsSync(DIR)})`);
  } catch (e) {
    console.log(`\nWARNING: could not remove ${DIR}: ${e.message}`);
  }
}

// ── summary ──────────────────────────────────────────────────────────────────
console.log('\nOBSERVED PER CASE');
for (const o of observations) {
  const { case: c, ...rest } = o;
  console.log(`  case ${c}: ${Object.entries(rest).map(([k, v]) => `${k}=${v}`).join(' ')}`);
}
console.log(`\nTOTAL  pass=${counts.pass} fail=${counts.fail} info=${counts.info}`);
console.log(counts.fail === 0 ? 'RESULT  ALL PASS' : `RESULT  ${counts.fail} FAILURE(S)`);
process.exit(counts.fail === 0 ? 0 : 1);
