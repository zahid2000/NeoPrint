// Design lint: no hardcoded colour, font size or spacing in the three UIs
// (CLAUDE.md "Dizayn", docs/DESIGN_SYSTEM.md). Every such value comes from the
// token files, so a redesign is a token change and nothing else.
//
//   node tools/design-lint          report and exit 1 on any finding
//   node tools/design-lint --list   also print every finding's line
//
// No dependencies by design (plan section 2.0). A line that has a real reason
// to break the rule says so with `design-lint: allow <reason>` in a comment.

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

import { rulesFor } from './rules.js';

export const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');

/** Where each app keeps its UI code, and the token files that are allowed literals. */
export const targets = [
  {
    app: 'admin',
    dir: 'admin/src',
    extensions: ['.scss', '.css'],
    skip: ['admin/src/styles/tokens.css'],
  },
  {
    app: 'mobile',
    dir: 'mobile/lib',
    extensions: ['.dart'],
    skip: ['mobile/lib/core/theme/tokens.dart', 'mobile/lib/l10n/'],
  },
  {
    app: 'kiosk',
    dir: 'desktop/src/NeoPrint.Kiosk',
    extensions: ['.axaml'],
    skip: ['desktop/src/NeoPrint.Kiosk/Styles/Tokens.axaml', 'desktop/src/NeoPrint.Kiosk/bin/', 'desktop/src/NeoPrint.Kiosk/obj/'],
  },
];

function* walk(dir) {
  for (const name of readdirSync(dir)) {
    if (name === 'node_modules' || name === 'bin' || name === 'obj' || name.startsWith('.')) continue;
    const full = join(dir, name);
    if (statSync(full).isDirectory()) yield* walk(full);
    else yield full;
  }
}

export function lintText(app, text) {
  const rules = rulesFor(app);
  const findings = [];

  text.split(/\r?\n/).forEach((line, index) => {
    if (/design-lint:\s*allow\b/.test(line)) return;

    for (const rule of rules) {
      for (const match of line.matchAll(rule.pattern)) {
        if (rule.accept && rule.accept(match, line)) continue;
        findings.push({ line: index + 1, rule: rule.name, text: match[0].trim() });
      }
    }
  });

  return findings;
}

export function lintRepository() {
  const findings = [];

  for (const target of targets) {
    for (const file of walk(join(root, target.dir))) {
      const rel = relative(root, file).split(sep).join('/');
      if (!target.extensions.some((ext) => rel.endsWith(ext))) continue;
      if (target.skip.some((skip) => rel === skip || rel.startsWith(skip))) continue;

      for (const finding of lintText(target.app, readFileSync(file, 'utf8'))) {
        findings.push({ app: target.app, file: rel, ...finding });
      }
    }
  }

  return findings;
}
