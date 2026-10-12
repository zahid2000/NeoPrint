import assert from 'node:assert/strict';
import { test } from 'node:test';

import { cellsOf, lint } from '../lib/registry.js';

const HEADER = [
  '| ID | Tarix | Addım | Tapşırıq (qısa) | Status | Sübut (commit / test / fayl) |',
  '|---|---|---|---|---|---|',
];

const table = (...rows) => [...HEADER, ...rows].join('\n');
const messages = (text) => lint(text).map((problem) => problem.message);

test('a well formed registry has nothing to report', () => {
  const text = table(
    '| T-001 | 2026-10-01 | 1 | First | Tamamlandı | `abc1234` |',
    '| T-002 | 2026-10-02 | 7A | Second | Açıq | |',
    '| T-003 | 2026-10-02 | 7A | Third | Təxirə salındı (Faza 2) | Qeydə alındı. |',
    '| T-004 | 2026-10-02 | 7 | Fourth | Ləğv edildi (səbəb: təkrar) | |',
    '| T-005 | 2026-10-02 | 7 | Fifth | İcrada | |',
  );

  assert.deepEqual(lint(text), []);
});

test('an escaped pipe stays inside its cell', () => {
  const line = '| T-001 | 2026-10-01 | 1 | `docker logs api \\| grep -c token` | Açıq | |';

  assert.equal(cellsOf(line).length, 6);
  assert.deepEqual(lint(table(line)), []);
});

test('a bare pipe inside a cell is reported with its line and task', () => {
  const problems = lint(table('| T-001 | 2026-10-01 | 1 | a | b | Tamamlandı | proof |'));

  assert.equal(problems.length, 1);
  assert.equal(problems[0].line, 3);
  assert.equal(problems[0].id, 'T-001');
  assert.match(problems[0].message, /7 cells instead of 6/);
});

test('a status that is not one of the five is reported', () => {
  assert.match(messages(table('| T-001 | 2026-10-01 | 1 | a | Done | proof |'))[0], /the status "Done"/);
  assert.match(messages(table('| T-001 | 2026-10-01 | 1 | a | Tamamlandıq | proof |'))[0], /the status/);
});

test('a finished task needs its evidence', () => {
  assert.deepEqual(messages(table('| T-001 | 2026-10-01 | 1 | a | Tamamlandı | |')), ['a finished task has no evidence']);
});

test('an id is used once', () => {
  const text = table('| T-001 | 2026-10-01 | 1 | a | Açıq | |', '| T-001 | 2026-10-01 | 1 | b | Açıq | |');

  assert.deepEqual(messages(text), ['the id is already used on line 3']);
});

test('a date that is not a date is reported', () => {
  assert.match(messages(table('| T-001 | 12.10.2026 | 1 | a | Açıq | |'))[0], /is not YYYY-MM-DD/);
});

test('a registry without its header or its tasks is reported', () => {
  assert.match(messages('# nothing here')[0], /header/);
  assert.deepEqual(messages(HEADER.join('\n')), ['the table has no tasks']);
});
