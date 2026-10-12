// Development/CI-only check for the runtime usage expression. It extracts the exact
// production expression from CodexRuntimeUsageReader.cs and runs it against synthetic
// React fiber trees; the shipped synthetic self-test stays Node-free because this covers
// page-side JavaScript that can only execute inside Codex.
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const sourcePath = join(dirname(fileURLToPath(import.meta.url)), '../src/CodexTokenOverlay/CodexRuntimeUsageReader.cs');
const match = readFileSync(sourcePath, 'utf8').match(/UsageExpression = """\r?\n([\s\S]*?)\r?\n {8}""";/);
if (!match) {
  console.error('FAIL: runtime usage expression not found in CodexRuntimeUsageReader.cs');
  process.exit(1);
}
const evaluate = page => new Function('window', 'return (' + match[1] + ')')(page);

let passed = 0;
const failed = [];
const check = (name, condition) => { if (condition) passed++; else failed.push(name); };

const normalOf = n => '00000000-0000-4000-8000-' + String(n).padStart(12, '0');
const sideOf = n => 'aaaa0000-0000-4000-8000-' + String(n).padStart(12, '0');
const bigOf = n => 'dddd0000-0000-4000-8000-' + String(n).padStart(12, '0');
const state = (id, side, tokens) => ({ id, sideConversation: side, latestTokenUsageInfo: { total: {
  inputTokens: tokens - 5, cachedInputTokens: 0, cacheWriteInputTokens: 0, outputTokens: 5,
  reasoningOutputTokens: 0, totalTokens: tokens } } });
const hook = value => ({ memoizedState: value, next: null });
const fiber = (value, child = null, sibling = null) => ({ memoizedState: hook(value), child, sibling });
const pageWith = store => ({ codexWindowType: 'electron',
  __codexRoot: { _internalRoot: { current: fiber(store ? { conversations: store } : {}) } } });
const storeOf = entries => new Map(entries.map(([id, side, tokens]) => [id, state(id, side, tokens)]));

{
  const entries = [];
  for (let i = 0; i < 64; i++) entries.push([normalOf(i), false, 20]);
  entries.push(['cccccccc-3333-4333-8333-333333333333', true, 30]);
  const result = evaluate(pageWith(storeOf(entries)));
  check('64 normal conversations cannot displace a later side chat',
    result !== null && result.truncated === false &&
    result.observations.some(o => o.id === 'cccccccc-3333-4333-8333-333333333333' && o.side === true));
}

{
  const entries = [];
  for (let i = 0; i < 40; i++) entries.push([sideOf(i), true, 10]);
  for (let i = 0; i < 60; i++) entries.push([normalOf(i), false, 10]);
  const result = evaluate(pageWith(storeOf(entries)));
  check('side chats take the result slots before normal sessions',
    result !== null && result.observations.length === 64 && result.observations.filter(o => o.side).length === 40 &&
    result.observations.slice(0, 40).every(o => o.side) && result.truncated === false);
}

{
  const entries = [];
  for (let i = 0; i < 65; i++) entries.push([sideOf(i), true, 10]);
  const result = evaluate(pageWith(storeOf(entries)));
  check('a side chat beyond the result budget reports truncation',
    result !== null && result.observations.length === 64 && result.truncated === true && result.reason.includes('侧聊'));
}

{
  const entries = [];
  for (let i = 0; i < 257; i++) entries.push([bigOf(i), i >= 256, 10]);
  const result = evaluate(pageWith(storeOf(entries)));
  check('a store beyond the size budget reports truncation',
    result !== null && result.observations.length === 0 && result.truncated === true && result.reason.includes('store'));
}

{
  const result = evaluate(pageWith(storeOf([[normalOf(0), false, 42]])));
  check('normal sessions alone are a complete observation with zero side chats',
    result !== null && result.observations.length === 1 && result.observations[0].side === false &&
    result.truncated === false && result.reason === null);
}

check('a page without a recognizable store reports nothing to observe', evaluate(pageWith(null)) === null);
check('a page that is not the Codex desktop shell reports nothing to observe', evaluate({ codexWindowType: 'browser' }) === null);

{
  let node = fiber({});
  for (let i = 0; i < 20005; i++) node = fiber({}, node);
  const result = evaluate({ codexWindowType: 'electron', __codexRoot: { _internalRoot: { current: node } } });
  check('an exhausted traversal reports truncation instead of a false empty',
    result !== null && result.observations.length === 0 && result.truncated === true && result.reason.includes('遍历'));
}

if (failed.length > 0) {
  for (const name of failed) console.error('FAIL: ' + name);
  console.error('runtime usage expression checks failed: ' + failed.length + ' of ' + (passed + failed.length));
  process.exit(1);
}
console.log('runtime usage expression checks passed: ' + passed);
