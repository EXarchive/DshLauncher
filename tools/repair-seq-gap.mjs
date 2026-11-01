import { readFrames, readSession } from './dsh-session-log.mjs';
import { zstdCompressSync, zstdDecompressSync, constants } from 'node:zlib';
import { readFileSync, writeFileSync, copyFileSync, statSync, existsSync } from 'node:fs';

const PATH = 'C:/Users/Administrator/.dsh/sessions/--C-Users-Administrator-PycharmProjects--/session-e6218840-d557-4a41-889b-21051e12fc9f/session.jsonl.zstd';
const CHECKSUM = { params: { [constants.ZSTD_c_checksumFlag]: 1 } };
const BATCH_LINES = 4096;

// 1. Decompress every frame into one plaintext buffer.
const buf = readFileSync(PATH);
const walk = readFrames(PATH);
if (walk.tornOffset !== null) {
  console.error('ABORT: file has a torn frame:', walk.tornReason);
  process.exit(1);
}
const parts = [];
for (const f of walk.frames) {
  parts.push(zstdDecompressSync(buf.subarray(f.offset, f.offset + f.compressed)));
}
const plain = Buffer.concat(parts);

// 2. Split into lines (each line ends with '\n').
const text = plain.toString('utf8');
const lines = text.split('\n');
// drop a trailing empty element produced by the final newline
if (lines.length && lines[lines.length - 1] === '') lines.pop();
console.log('total lines:', lines.length);

// 3. Verify header and the target line.
console.log('line0 prefix:', lines[0].slice(0, 80));
const target = JSON.parse(lines[22614]);
console.log('line 22614:', JSON.stringify(target));
if (target.type !== 'session/end-seed' || target.seq !== 692882) {
  console.error('ABORT: line 22614 is not the expected end-seed@692882');
  process.exit(1);
}
const nxt = JSON.parse(lines[22615]);
if (nxt.type !== 'agent/inbox/spliced' || nxt.seq !== 692882) {
  console.error('ABORT: line 22615 is not the expected spliced@692882');
  process.exit(1);
}

// 4. Remove line 22614 (the stale end-seed).
lines.splice(22614, 1);
console.log('after removal, lines:', lines.length, '| new line 22614:', lines[22614].slice(0, 80));

// 5. Re-encode: header frame + event batch frames.
const headerLine = lines[0];
const eventLines = lines.slice(1);
const headerFrame = zstdCompressSync(Buffer.from(headerLine + '\n', 'utf8'), CHECKSUM);
const frames = [headerFrame];
for (let i = 0; i < eventLines.length; i += BATCH_LINES) {
  const batch = eventLines.slice(i, i + BATCH_LINES).join('\n') + '\n';
  frames.push(zstdCompressSync(Buffer.from(batch, 'utf8'), CHECKSUM));
}
const out = Buffer.concat(frames);
console.log('original bytes:', buf.length, '| repaired bytes:', out.length, '| frames:', frames.length);

// 6. Backup original, write repaired (skip when DRY=1).
const DRY = process.env.DRY === '1';
if (!DRY) {
  const ts = Date.now();
  const bak = `${PATH}.bak-seqgap-${ts}`;
  copyFileSync(PATH, bak);
  writeFileSync(PATH, out);
  console.log('backup written:', bak);
  console.log('repaired file written:', PATH, '(', out.length, 'bytes )');
} else {
  console.log('DRY RUN: file NOT modified');
}

// 7. Verify repaired bytes: frames intact + no seq gaps.
//    In dry mode, write to a temp file so readFrames/readSession can read it back.
const verifyPath = DRY ? `${PATH}.dryrun-${Date.now()}` : PATH;
if (DRY) writeFileSync(verifyPath, out);
const walk2 = readFrames(verifyPath);
console.log('verify tornOffset (should be null):', walk2.tornOffset, walk2.tornReason ?? '');
const s2 = readSession(verifyPath);
console.log('verify events:', s2.stats.events, 'frames:', s2.stats.frames, 'unparsable:', s2.stats.unparsableLines);

// replicate DSH seq-contiguity check
const CHUNK = new Set(['text-chunks', 'reasoning-chunks', 'tool-call-chunks']);
let expected = 0;
let gaps = 0;
for (let i = 0; i < s2.events.length; i++) {
  const rec = s2.events[i];
  if (i === 0 && rec.type === 'session' && rec.seq === undefined) continue;
  const startSeq = CHUNK.has(rec.type) ? rec.seq0 : rec.seq;
  const count = CHUNK.has(rec.type) ? (rec.type === 'tool-call-chunks' ? rec.data.args.length : rec.data.texts.length) : 1;
  if (startSeq !== expected) {
    if (gaps < 10) console.log(`GAP line ${i} type=${rec.type} seq=${startSeq} expected=${expected}`);
    gaps++;
  }
  expected += count;
}
console.log('verify seq gaps (should be 0):', gaps, '| decoded events:', expected);
