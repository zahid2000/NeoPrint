// ---------------------------------------------------------------------------
// The Material Symbols subset the panel ships (docs/DECISIONS.md: fonts are
// self-hosted, the icon font cut down to the icons actually used).
//
//   node scripts/icon-subset.mjs           rebuild the font from the sources
//   node scripts/icon-subset.mjs --check   fail if a used icon is missing (CI)
//
// An icon outside the subset renders as its name in plain text, which looks
// like a bug and is easy to miss in review - hence the check.
//
// Icons are found in <mat-icon> contents (including quoted names in
// expressions there), and in `icon: '...'` / `icon="..."` values. Anything
// chosen at run time from elsewhere goes in EXTRA below.
// ---------------------------------------------------------------------------
import { readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const root = new URL('..', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');
const listFile = join(root, 'public', 'fonts', 'material-symbols-rounded.icons.txt');
const fontFile = join(root, 'public', 'fonts', 'material-symbols-rounded.woff2');

/** Icons picked at run time from maps the scan cannot see. */
const EXTRA = [
  // status-chip.component.ts tones
  'check_circle',
  'warning',
  'error',
  'radio_button_unchecked',
  'progress_activity',
  // live-indicator.component.ts states
  'sync',
  'link_off',
];

function files(dir) {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? files(path) : [path];
  });
}

function usedIcons() {
  const found = new Set(EXTRA);
  const name = /^[a-z0-9_]+$/;

  for (const file of files(join(root, 'src'))) {
    if (!/\.(html|ts)$/.test(file) || file.endsWith('.spec.ts')) {
      continue;
    }

    const text = readFileSync(file, 'utf8');

    // Prettier sometimes breaks the closing tag: `</mat-icon\n  >`.
    for (const [, content] of text.matchAll(/<mat-icon[^>]*>([\s\S]*?)<\/mat-icon\s*>/g)) {
      const plain = content.trim();

      if (name.test(plain)) {
        found.add(plain);
      }

      // Quoted names are what an expression picks; a quoted word something
      // is compared with (`theme() === 'dark'`) is not an icon.
      for (const [, quoted] of plain.matchAll(/(?<![=!]==\s*)'([a-z0-9_]+)'/g)) {
        found.add(quoted);
      }
    }

    // A component's default icon: `icon = input('inbox')`.
    for (const [, icon] of text.matchAll(/\bicon\s*=\s*input\(\s*'([a-z0-9_]+)'/g)) {
      found.add(icon);
    }

    for (const [, icon] of text.matchAll(/\bicon\s*[:=]\s*["']([a-z0-9_]+)["']/g)) {
      found.add(icon);
    }

    for (const [, icon] of text.matchAll(/\bicon\s*=\s*"([a-z0-9_]+)"/g)) {
      found.add(icon);
    }
  }

  return [...found].sort();
}

const icons = usedIcons();

if (process.argv.includes('--check')) {
  const shipped = new Set(readFileSync(listFile, 'utf8').split(/\s+/).filter(Boolean));
  const missing = icons.filter((icon) => !shipped.has(icon));

  if (missing.length > 0) {
    console.error(`Icons used but not in the font subset: ${missing.join(', ')}`);
    console.error('Run: node scripts/icon-subset.mjs');
    process.exit(1);
  }

  console.log(`Icon subset OK: ${icons.length} icons used, all shipped.`);
  process.exit(0);
}

// Google Fonts cuts the font to the listed icons; the names must be sorted.
const url =
  'https://fonts.googleapis.com/css2?family=Material+Symbols+Rounded:opsz,wght,FILL,GRAD@24,400,0,0' +
  `&icon_names=${icons.join(',')}&display=block`;

const css = await (
  await fetch(url, {
    headers: { 'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36' },
  })
).text();

const fontUrl = css.match(/url\((https:[^)]+)\)/)?.[1];

if (!fontUrl) {
  console.error('Google Fonts did not return a font URL:\n' + css.slice(0, 500));
  process.exit(1);
}

const font = Buffer.from(await (await fetch(fontUrl)).arrayBuffer());

writeFileSync(fontFile, font);
writeFileSync(listFile, icons.join('\n') + '\n');

console.log(`Wrote ${icons.length} icons, ${(font.length / 1024).toFixed(1)} KB.`);
