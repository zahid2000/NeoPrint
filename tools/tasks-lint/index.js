#!/usr/bin/env node
// node tools/tasks-lint [path] - checks the rows of docs/TASKS.md (T-247).
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { cellsOf, lint } from './lib/registry.js';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const path = resolve(process.argv[2] ?? resolve(root, 'docs', 'TASKS.md'));
const text = readFileSync(path, 'utf8');
const problems = lint(text);

for (const { line, id, message } of problems) {
  console.error(`${path}:${line}${id ? ` ${id}` : ''}: ${message}`);
}

if (problems.length > 0) {
  console.error(`\n${problems.length} problem(s) in the task registry.`);
  process.exit(1);
}

const tasks = text.split(/\r?\n/).filter((line) => /^\s*\|\s*T-\d+\b/.test(line));
const open = tasks.filter((line) => /^(Açıq|İcrada)/.test(cellsOf(line)[4])).length;
console.log(`${tasks.length} tasks, ${open} open or in progress; every row is well formed.`);
