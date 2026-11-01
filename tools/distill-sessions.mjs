/**
 * distill-sessions.mjs — turn the mined corpus into reusable lessons.
 *
 * mine-sessions.mjs produces raw evidence; this turns it into rules. The method
 * is deliberately mechanical: cluster failure texts by a normalised signature so
 * a lesson is only reported when it recurs, and quote the real message so the
 * rule can be checked against reality instead of being taken on faith.
 *
 * Usage: node tools/distill-sessions.mjs [--in DIR] [--out FILE] [--top N]
 */

import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const argv = process.argv.slice(2);
const arg = (n, d) => { const i = argv.indexOf(n); return i >= 0 && argv[i + 1] ? argv[i + 1] : d; };
const IN = arg('--in', join(process.cwd(), '_analysis'));
const OUT = arg('--out', join(IN, 'lessons.md'));
const TOP = Number(arg('--top', '60'));

const itemsPath = join(IN, 'items.jsonl');
if (!existsSync(itemsPath)) {
  console.error(`no corpus at ${itemsPath} — run mine-sessions.mjs first`);
  process.exit(1);
}

const items = readFileSync(itemsPath, 'utf8').split('\n').filter(Boolean).map((l) => {
  try { return JSON.parse(l); } catch { return null; }
}).filter(Boolean);

/**
 * Normalise a failure message into a signature so that variants of the same
 * error collapse into one cluster: paths, ids, numbers and timings are removed.
 */
function signature(text) {
  let s = text;
  // keep the tool tag separate so the same error under two tools stays distinct
  const tool = (s.match(/^\[([a-z_]+)\]/) ?? [])[1] ?? '?';
  s = s.replace(/^\[[a-z_]+\]\s*/, '');

  // Most specific first: recognisable error classes.
  const known = [
    [/\[exit code:\s*(\d+)\]/, (m) => `exit code ${m[1]}`],
    [/\bENOENT\b[^\n]{0,80}/, (m) => `ENOENT ${m[0].replace(/['"][^'"]*['"]/g, "'<path>'").slice(0, 70)}`],
    [/\bEPERM\b[^\n]{0,60}/, () => 'EPERM (blocked by sandbox/policy)'],
    [/\bEACCES\b[^\n]{0,60}/, () => 'EACCES (access denied)'],
    [/\bEBUSY\b[^\n]{0,60}/, () => 'EBUSY (file locked by another process)'],
    [/error CS\d+:\s*([^\n]{0,90})/, (m) => `C# compile: ${m[1]}`],
    [/error NU\d+:\s*([^\n]{0,90})/, (m) => `NuGet: ${m[1]}`],
    [/ParserError[^\n]{0,90}/, () => 'PowerShell ParserError (syntax)'],
    [/MethodInvocationException[^\n]{0,80}/, () => 'PowerShell MethodInvocationException'],
    [/ToolArgsError[^\n]{0,90}/, () => 'tool argument validation'],
    [/returned invalid output[^\n]{0,90}/, () => 'tool OUTPUT schema validation'],
    [/Traceback \(most recent call last\)[\s\S]{0,200}?(\w+Error[^\n]{0,80})/, (m) => `Python ${m[1]}`],
    [/(?:TypeError|ReferenceError|SyntaxError|RangeError):[^\n]{0,90}/, (m) => `JS ${m[0]}`],
    [/network:\s*[^\n]{0,70}/i, (m) => `network policy: ${m[0].slice(0, 70)}`],
    [/sandbox:[^\n]{0,70}/i, () => 'sandbox denial'],
    [/timed out[^\n]{0,50}/i, () => 'timeout'],
    [/approval[^\n]{0,50}/i, () => 'approval required/rejected'],
    [/rejected[^\n]{0,50}/i, () => 'rejected'],
  ];
  for (const [re, fmt] of known) {
    const m = s.match(re);
    if (m) return `${tool} | ${fmt(m)}`;
  }

  // Fall back to the first meaningful line, stripped of volatile detail.
  const line = s.split('\n').map((x) => x.trim()).find((x) => x.length > 6) ?? s;
  return `${tool} | ${line
    .replace(/[A-Z]:\\[^\s"']+/g, '<path>')
    .replace(/\/[^\s"']+/g, '/<path>')
    .replace(/\b[0-9a-f]{8,}\b/gi, '<id>')
    .replace(/\b\d+(\.\d+)?\b/g, '<n>')
    .slice(0, 90)}`;
}

const failures = items.filter((i) => i.kind === 'tool-failure');
const clusters = new Map();
for (const f of failures) {
  const sig = signature(f.text);
  if (!clusters.has(sig)) clusters.set(sig, { sig, count: 0, samples: [], sessions: new Set(), first: null, last: null });
  const c = clusters.get(sig);
  c.count += 1;
  c.sessions.add(f.sessionId);
  if (c.samples.length < 2) c.samples.push(f.text.replace(/\s+/g, ' ').slice(0, 260));
  if (f.date && (!c.first || f.date < c.first)) c.first = f.date;
  if (f.date && (!c.last || f.date > c.last)) c.last = f.date;
}

const ranked = [...clusters.values()].sort((a, b) => b.count - a.count).slice(0, TOP);

// Failure rate per tool, from the session index (toolCalls) plus the failures.
const index = JSON.parse(readFileSync(join(IN, 'session-index.json'), 'utf8'));
const callsPerTool = new Map();
const failPerTool = new Map();
for (const c of clusters.values()) {
  const t = c.sig.split(' | ')[0];
  failPerTool.set(t, (failPerTool.get(t) ?? 0) + c.count);
}
for (const f of failures) {
  const t = (f.text.match(/^\[([a-z_]+)\]/) ?? [])[1];
  if (t) failPerTool.set(t, failPerTool.get(t) ?? 0);
}
// authoritative call counts come from the report we already produced; recompute
// from the index is not possible (it stores toolCalls total), so re-use items.
const callCounts = new Map();
for (const i of items) {
  const m = i.kind === 'tool-failure' ? i.text.match(/^\[([a-z_]+)\]/) : null;
  if (m) callCounts.set(m[1], (callCounts.get(m[1]) ?? 0) + 1);
}

const recurring = items.filter((i) => i.kind === 'user-request');
const requestWords = new Map();
const STOP = new Set(['the','and','for','you','this','that','with','from','have','are','not','but','all','can','will','一个','这个','我们','现在','可以','需要','已经','如果','然后','还有','不要','不能','没有','什么','怎么','为什么','因为','所以','就是','这样','那样','帮我','请','的','了','是','在','我','你','他','它','和','与','或']);
for (const r of recurring) {
  for (const w of r.text.toLowerCase().split(/[^0-9a-zA-Z\u4e00-\u9fa5_]+/)) {
    if (w.length < 2 || STOP.has(w)) continue;
    requestWords.set(w, (requestWords.get(w) ?? 0) + 1);
  }
}

const md = [];
md.push('# DSH session corpus — extracted experience');
md.push('');
md.push(`Derived mechanically from ${items.length} extracted items across ${index.length} session logs`);
md.push('(see `session-index.json` / `items.jsonl`; regenerate with `tools/mine-sessions.mjs`).');
md.push('A lesson is listed only when the same failure signature recurred; each carries a real sample.');
md.push('');
md.push('## 1. Failure signatures by frequency');
md.push('');
md.push('| # | count | sessions | signature | first seen | last seen |');
md.push('|---:|---:|---:|---|---|---|');
ranked.forEach((c, i) => {
  md.push(`| ${i + 1} | ${c.count} | ${c.sessions.size} | \`${c.sig.replace(/\|/g, '\\|')}\` | ${(c.first ?? '').slice(0, 10)} | ${(c.last ?? '').slice(0, 10)} |`);
});
md.push('');
md.push('## 2. Representative messages (verbatim, for verification)');
md.push('');
for (const c of ranked.slice(0, 25)) {
  md.push(`### ${c.sig}`);
  md.push(`*${c.count} occurrences across ${c.sessions.size} session(s)*`);
  for (const s of c.samples) md.push('```\n' + s + '\n```');
}
md.push('');
md.push('## 3. Recurring terms in user requests');
md.push('');
md.push([...requestWords.entries()].sort((a, b) => b[1] - a[1]).slice(0, 60)
  .map(([w, n]) => `${w}(${n})`).join(' · '));
md.push('');

writeFileSync(OUT, md.join('\n'), 'utf8');
writeFileSync(join(IN, 'failures-ranked.json'), JSON.stringify(
  ranked.map((c) => ({ ...c, sessions: [...c.sessions] })), null, 2), 'utf8');

console.log(`clusters: ${clusters.size}   failures: ${failures.length}   items: ${items.length}`);
console.log(`top 20 signatures:`);
for (const c of ranked.slice(0, 20)) {
  console.log(`  ${String(c.count).padStart(5)}  ${c.sessions.size.toString().padStart(3)} sess  ${c.sig}`);
}
console.log(`\nwritten: ${OUT}`);
