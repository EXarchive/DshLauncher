import { readSession } from './dsh-session-log.mjs';

const P = 'C:/Users/Administrator/.dsh/sessions/--C-Users-Administrator-PycharmProjects--/session-e6218840-d557-4a41-889b-21051e12fc9f/session.jsonl.zstd';
const s = readSession(P);

// raw lines are the storage records (some are chunk rows). Decode them the way
// DSH's decodeStorageRecord does, tracking the expected contiguous seq.
const CHUNK = new Set(['text-chunks', 'reasoning-chunks', 'tool-call-chunks']);
let expected = 0;
const gaps = [];
const seen = []; // decoded event type -> seq, for context
let totalDecoded = 0;

function memberCount(rec) {
  return rec.type === 'tool-call-chunks' ? rec.data.args.length : rec.data.texts.length;
}

for (let i = 0; i < s.events.length; i++) {
  const rec = s.events[i];
  if (i === 0 && rec.type === 'session' && rec.seq === undefined) continue; // header record, not an event
  let startSeq, count;
  if (CHUNK.has(rec.type)) {
    startSeq = rec.seq0;
    count = memberCount(rec);
  } else {
    startSeq = rec.seq;
    count = 1;
  }
  if (startSeq !== expected) {
    gaps.push({
      line: i,            // 0-based raw line index
      type: rec.type,
      startSeq,
      expected,
      count,
      delta: startSeq - expected,
      prevTypes: seen.slice(-6).map((e) => `${e.type}@${e.seq}`),
    });
  }
  // advance
  for (let k = 0; k < count; k++) {
    seen.push({ type: rec.type, seq: startSeq + k });
  }
  if (seen.length > 2000) seen.splice(0, seen.length - 2000);
  expected += count;
  totalDecoded += count;
}

console.log('rawLines', s.events.length, 'decodedEvents', totalDecoded, 'gaps', gaps.length);
for (const g of gaps.slice(0, 60)) {
  console.log(`line ${g.line} type=${g.type} seq=${g.startSeq} expected=${g.expected} delta=${g.delta} count=${g.count}`);
  console.log('   prev: ' + g.prevTypes.join('  '));
}
