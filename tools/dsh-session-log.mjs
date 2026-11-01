/**
 * dsh-session-log.mjs — read, validate and repair DSH session logs.
 *
 * THE FORMAT
 * ----------
 * A DSH session log is a CONCATENATED-FRAME Zstandard container:
 *
 *     [frame 0][frame 1][frame 2]...[frame N]
 *
 * Each frame is an independent zstd frame whose content is a UTF-8 JSONL batch.
 * Frame 0 is always the session header (`{"type":"session", ...}`); later frames
 * carry events such as `turn/start`, `tool/call`, `message`.
 *
 * Frame boundaries matter and cannot be found by scanning for the zstd magic
 * number: 0x28B52FFD can occur inside compressed payloads. The only correct way
 * is to parse each frame's header and walk its blocks — which is what DSH itself
 * does when it reports "invalid frame magic at byte N".
 *
 * WHY NOT zstdDecompressSync ON THE WHOLE FILE
 * --------------------------------------------
 * Node's one-shot and streaming zstd decoders both stop after the FIRST frame,
 * so a 3.5 MB session log yields only its 172-byte header. Every frame must be
 * decoded individually.
 *
 * TORN FRAMES (the normal corruption, not an exotic one)
 * ------------------------------------------------------
 * An append is not atomic, so a crash mid-write leaves a final frame that is
 * partially present. That trailer is unrecoverable BY DEFINITION — its blocks
 * were never finished — and the correct repair is to drop it and keep every
 * complete frame before it. This module reports the exact byte offset to truncate
 * to and never invents data.
 *
 * Used by both the session manager and the experience miner, so the two can
 * never disagree about what a session contains.
 */

import { createHash } from 'node:crypto';
import { closeSync, existsSync, openSync, readFileSync, readSync, renameSync, statSync, writeFileSync } from 'node:fs';
import { zstdCompressSync, zstdDecompressSync } from 'node:zlib';

const ZSTD_MAGIC = 0xfd2fb528; // little-endian read of 28 B5 2F FD
const MAGIC_LE = Buffer.from([0x28, 0xb5, 0x2f, 0xfd]);

export const SESSION_HEADER_TYPE = 'session';

/**
 * Compute the total compressed size of the zstd frame starting at `off`.
 *
 * Walks: magic -> frame header descriptor -> window descriptor ->
 * dictionary id -> frame content size -> block headers until Last_Block ->
 * optional 4-byte xxhash content checksum.
 *
 * @returns {{ total: number, contentSize: number|null }} `total` is the frame's
 *   compressed length in bytes; `contentSize` is null when the frame header does
 *   not declare the decompressed size (legal; the decoder still knows it).
 */
export function frameSize(buf, off) {
  if (off + 5 > buf.length) throw new Error(`truncated frame header at byte ${off}`);
  if (buf.readUInt32LE(off) !== ZSTD_MAGIC) {
    throw new Error(`invalid frame magic at byte ${off}`);
  }

  let p = off + 4;
  const fhd = buf[p];
  p += 1;

  const fcsFlag = (fhd >> 6) & 3;
  const singleSegment = (fhd >> 5) & 1;
  const hasChecksum = (fhd >> 2) & 1;
  const dictFlag = fhd & 3;

  if (!singleSegment) p += 1;                 // Window_Descriptor
  p += [0, 1, 2, 4][dictFlag];                // Dictionary_ID

  const fcsSize = singleSegment ? [1, 2, 4, 8][fcsFlag] : [0, 2, 4, 8][fcsFlag];
  let contentSize = null;
  if (fcsSize === 1) {
    contentSize = buf[p];
  } else if (fcsSize === 2) {
    contentSize = buf.readUInt16LE(p) + 256;  // per spec: 2-byte FCS is offset by 256
  } else if (fcsSize === 4) {
    contentSize = buf.readUInt32LE(p);
  } else if (fcsSize === 8) {
    contentSize = Number(buf.readBigUInt64LE(p));
  }
  p += fcsSize;

  for (;;) {
    if (p + 3 > buf.length) throw new Error(`truncated block header at byte ${p}`);
    const blockHeader = buf.readUIntLE(p, 3);
    p += 3;
    const lastBlock = blockHeader & 1;
    const blockType = (blockHeader >> 1) & 3;
    const blockSize = blockHeader >> 3;

    if (blockType === 3) throw new Error(`reserved block type at byte ${p - 3}`);
    if (blockType === 1) p += 1;              // RLE: one byte of payload
    else p += blockSize;                      // Raw or Compressed

    if (lastBlock) break;
    if (p > buf.length) throw new Error(`truncated frame body at byte ${p}`);
  }

  if (hasChecksum) p += 4;
  if (p > buf.length) throw new Error(`truncated frame (declared end ${p} beyond ${buf.length})`);
  return { total: p - off, contentSize };
}

/**
 * Walk every frame in a session log.
 *
 * Never throws on corruption: a damaged file is described, not rejected. The
 * caller gets the good frames plus a precise diagnosis, because a partly
 * readable session is far more useful than an error.
 *
 * @returns {{
 *   frames: Array<{offset:number, compressed:number, bytes:number, lines:number, firstType:string|null, error:string|null}>,
 *   tornOffset: number|null, tornReason: string|null,
 *   goodBytes: number, totalBytes: number, sha256: string
 * }}
 */
export function readFrames(path) {
  const buf = readFileSync(path);
  const sha256 = createHash('sha256').update(buf).digest('hex');
  const frames = [];
  let off = 0;
  let tornOffset = null;
  let tornReason = null;

  // Frame 0 carries the session header, so a log with no bytes at all is not a
  // clean empty session: the walk below never starts (it begins and ends at 0)
  // and would otherwise report the file as intact. Say what is actually true.
  if (buf.length === 0) {
    tornOffset = 0;
    tornReason = 'empty file: no frame 0 (the session header is missing)';
  }

  while (off < buf.length) {
    let size;
    try {
      size = frameSize(buf, off);
    } catch (e) {
      tornOffset = off;
      tornReason = e.message;
      break;
    }

    let content = null;
    let error = null;
    try {
      content = zstdDecompressSync(buf.subarray(off, off + size.total));
    } catch (e) {
      // The frame is structurally complete but will not decode: keep walking is
      // pointless (the rest is likely desynchronised), so stop and report it.
      tornOffset = off;
      tornReason = `frame does not decompress: ${e.message}`;
      break;
    }

    const text = content.toString('utf8');
    const lines = text.split('\n').filter((l) => l.length > 0);
    let firstType = null;
    if (lines.length > 0) {
      try {
        firstType = JSON.parse(lines[0]).type ?? null;
      } catch {
        error = 'first line is not valid JSON';
      }
    }

    frames.push({
      offset: off,
      compressed: size.total,
      bytes: content.length,
      lines: lines.length,
      firstType,
      error,
    });
    off += size.total;
  }

  return {
    frames,
    tornOffset,
    tornReason,
    goodBytes: tornOffset ?? buf.length,
    totalBytes: buf.length,
    sha256,
  };
}

/**
 * Read a whole session: header plus every decodable event.
 *
 * @returns {{
 *   ok: boolean, id: string|null, cwd: string|null, createdAt: number|null,
 *   events: Array<object>, eventCounts: Record<string, number>,
 *   stats: object, corruption: object|null, path: string, bytes: number
 * }}
 */
export function readSession(path) {
  const walk = readFrames(path);
  const events = [];
  let unparsableLines = 0;

  const buf = readFileSync(path);
  for (const f of walk.frames) {
    let content;
    try {
      content = zstdDecompressSync(buf.subarray(f.offset, f.offset + f.compressed));
    } catch {
      continue;
    }
    for (const line of content.toString('utf8').split('\n')) {
      if (!line) continue;
      try {
        events.push(JSON.parse(line));
      } catch {
        unparsableLines += 1;
      }
    }
  }

  const header = events.find((e) => e.type === SESSION_HEADER_TYPE) ?? null;
  const eventCounts = {};
  for (const e of events) eventCounts[e.type] = (eventCounts[e.type] ?? 0) + 1;

  // ── structural validation, not just zstd framing ──────────────────────────
  //
  // A log can decompress perfectly and STILL be broken at the level DSH loads:
  // a duplicated `seq` makes DSH refuse the session. Gaps in seq are NORMAL
  // (the counter is instance-global and delegated runs splice large ranges), so
  // continuity is NOT checked — only uniqueness and monotonic order, which are
  // the things that actually fail to load.
  const seqSeen = new Map();
  const duplicateSeq = [];
  let outOfOrderSeq = 0;
  let lastSeq = -1;
  for (const e of events) {
    if (typeof e.seq !== 'number') continue;
    if (e.seq < lastSeq) outOfOrderSeq += 1;
    lastSeq = e.seq;
    const n = (seqSeen.get(e.seq) ?? 0) + 1;
    seqSeen.set(e.seq, n);
    if (n === 2) duplicateSeq.push(e.seq);
  }

  const structural = {
    duplicateSeq,
    outOfOrderSeq,
    // A healthy session ends each started turn/step and each tool/call has a
    // matching result; a log with an open turn or an unanswered tool call is a
    // sign an append died mid-event, even when every zstd frame is intact.
    openTurns: 0,
    openSteps: 0,
    unansweredToolCalls: 0,
  };
  const openTurns = new Set();
  const openSteps = new Set();
  const pendingCalls = new Set();
  for (const e of events) {
    if (e.type === 'turn/start' && typeof e.data?.turn === 'number') openTurns.add(e.data.turn);
    else if (e.type === 'turn/end' && typeof e.data?.turn === 'number') openTurns.delete(e.data.turn);
    else if (e.type === 'step/start' && typeof e.data?.step === 'number') openSteps.add(e.data.step);
    else if (e.type === 'step/end' && typeof e.data?.step === 'number') openSteps.delete(e.data.step);
    else if (e.type === 'tool/call') pendingCalls.add(e.data?.callId);
    else if (e.type === 'tool/result') pendingCalls.delete(e.data?.message?.source?.callId ?? e.data?.callId);
  }
  structural.openTurns = openTurns.size;
  structural.openSteps = openSteps.size;
  structural.unansweredToolCalls = pendingCalls.size;

  // Structural issues that ACTUALLY prevent DSH from loading the session:
  // a duplicated or out-of-order `seq`. Incomplete tails (openTurns/openSteps/
  // unansweredToolCalls) are reported for information but are NOT load-blocking —
  // a session killed mid-turn still loads, it just ends on a partial turn — so
  // they must NOT flip `ok` to false, or a crash would be mis-labelled damage.
  const structurallyBroken = duplicateSeq.length > 0 || outOfOrderSeq > 0;

  const corruption = walk.tornOffset === null
    ? null
    : {
        atByte: walk.tornOffset,
        reason: walk.tornReason,
        lostBytes: walk.totalBytes - walk.tornOffset,
        lostPercent: walk.totalBytes === 0
          ? 0
          : Math.round(((walk.totalBytes - walk.tornOffset) / walk.totalBytes) * 1000) / 10,
        repairable: walk.tornOffset > 0,
      };

  return {
    ok: corruption === null && unparsableLines === 0 && !structurallyBroken,
    id: header?.id ?? null,
    cwd: header?.cwd ?? null,
    createdAt: header?.createdAt ?? null,
    agentPreset: header?.agentPreset ?? null,
    events,
    eventCounts,
    stats: {
      events: events.length,
      frames: walk.frames.length,
      unparsableLines,
      bytes: walk.totalBytes,
      goodBytes: walk.goodBytes,
    },
    structural,
    corruption,
    sha256: walk.sha256,
    path,
  };
}

/**
 * Rewrite a session so every event carries a strictly increasing, unique `seq`,
 * preserving event order.
 *
 * This is the repair for a log whose zstd framing is fine but whose events carry
 * duplicated or out-of-order seq numbers — the thing that makes DSH itself refuse
 * to load the session. The original file is moved aside first, never deleted.
 *
 * @returns {{ repaired: boolean, reason: string, changed: number, backup: string|null }}
 */
export function renumberSessionSeq(path, { backup = true, now = Date.now() } = {}) {
  const sess = readSession(path);
  if (sess.structural.duplicateSeq.length === 0 && sess.structural.outOfOrderSeq === 0) {
    return { repaired: false, reason: 'seq numbers are already unique and in order', changed: 0, backup: null };
  }

  let nextSeq = 0;
  for (const e of sess.events) {
    if (typeof e.seq !== 'number') continue;
    if (e.seq >= nextSeq) nextSeq = e.seq + 1;
  }
  const rewrites = [];
  for (const e of sess.events) {
    if (typeof e.seq !== 'number') continue;
    // Give every event a fresh, increasing seq starting after the original max,
    // so the whole tail is cleanly re-anchored rather than patched in place.
    rewrites.push({ old: e.seq, neu: nextSeq++ });
    e.seq = nextSeq - 1;
  }

  let backupPath = null;
  if (backup) {
    backupPath = `${path}.seq-renumber-${new Date(now).toISOString().slice(0, 10).replace(/-/g, '')}-${now}`;
    renameSync(path, backupPath);
  }
  const jsonl = sess.events.map((e) => JSON.stringify(e)).join('\n') + '\n';
  // The file MUST stay a zstd container: writing plain JSONL to a `.zstd` file
  // would make DSH fail to read it ("invalid frame magic") — a repair that
  // corrupts. Compress the whole renumbered log as one frame; DSH's frame walker
  // reads a single-frame file fine and can keep appending frames after it.
  writeFileSync(path, zstdCompressSync(Buffer.from(jsonl, 'utf8')));

  const after = readSession(path);
  return {
    repaired: after.structural.duplicateSeq.length === 0 && after.structural.outOfOrderSeq === 0,
    reason: after.ok
      ? `renumbered ${rewrites.length} event(s) to a clean increasing sequence`
      : 'renumbering did not produce a clean sequence; the original backup is intact',
    changed: rewrites.length,
    backup: backupPath,
  };
}

/**
 * Repair a session log by dropping an unreadable trailer.
 *
 * This is the ONLY honest repair available for a torn frame: the missing blocks
 * were never written, so nothing can reconstruct them. Everything before the
 * break is kept byte-for-byte, and the original is moved aside rather than
 * deleted — a repair that destroys the evidence is not a repair.
 *
 * @returns {{ repaired: boolean, reason: string, before: number, after: number,
 *             backup: string|null }}
 */
export function repairSession(path, { backup = true, now = Date.now() } = {}) {
  const walk = readFrames(path);
  const size = walk.totalBytes;

  if (walk.tornOffset === null) {
    return { repaired: false, reason: 'log is intact, nothing to repair', before: size, after: size, backup: null };
  }
  if (walk.tornOffset === 0) {
    return {
      repaired: false,
      reason: `the very first frame is unreadable (${walk.tornReason}); there is no intact prefix to keep`,
      before: size,
      after: size,
      backup: null,
    };
  }

  // Verify the truncation point really is a clean frame boundary before touching
  // anything: drop the trailer and re-read; only then is the result trusted.
  const keep = walk.tornOffset;
  let backupPath = null;
  if (backup) {
    backupPath = `${path}.corrupt-${new Date(now).toISOString().slice(0, 10).replace(/-/g, '')}-${now}`;
    renameSync(path, backupPath);
    writeFileSync(path, readFileSync(backupPath).subarray(0, keep));
  } else {
    const fd = openSync(path, 'r+');
    try {
      const keepBuf = Buffer.alloc(keep);
      readSync(fd, keepBuf, 0, keep, 0);
      closeSync(fd);
      writeFileSync(path, keepBuf);
    } catch (e) {
      try { closeSync(fd); } catch { /* already closed */ }
      throw e;
    }
  }

  const after = readFrames(path);
  const verified = after.tornOffset === null;

  return {
    repaired: verified,
    reason: verified
      ? `dropped an unreadable trailer (${walk.tornReason})`
      : 'truncation did not produce a clean log; the original backup is intact',
    before: size,
    after: statSync(path).size,
    droppedBytes: size - keep,
    backup: backupPath,
  };
}

/** The canonical on-disk location of a session log. */
export function sessionLogPath(sessionsRoot, workspaceDir, sessionId) {
  return `${sessionsRoot}\\${workspaceDir}\\${sessionId}\\session.jsonl.zstd`;
}

export function exists(path) {
  return existsSync(path);
}

export { MAGIC_LE };
