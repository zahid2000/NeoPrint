#!/usr/bin/env node
// Design lint: no hardcoded colour, font size or spacing in the three UIs
// (CLAUDE.md "Dizayn", docs/DESIGN_SYSTEM.md). Every such value comes from the
// token files, so a redesign is a token change and nothing else.
//
//   node tools/design-lint          report and exit 1 on any finding
//   node tools/design-lint --list   print every finding's line
//
// No dependencies by design (plan section 2.0). A line with a real reason to
// break the rule says so in a comment: `design-lint: allow <reason>`.

import { lintRepository, targets } from './lib/lint.js';

const findings = lintRepository();
const list = process.argv.includes('--list') || findings.length <= 50;

if (list) {
  for (const f of findings) console.log(`${f.file}:${f.line}  ${f.rule}  ${f.text}`);
}

for (const target of targets) {
  const count = findings.filter((f) => f.app === target.app).length;
  console.log(`${target.app}: ${count} finding(s)`);
}

if (findings.length > 0) {
  console.log('\nUse a token instead (docs/DESIGN_SYSTEM.md), or mark a justified exception with "design-lint: allow <reason>".');
  process.exit(1);
}

console.log('PASS — no hardcoded colour, font size or spacing.');
