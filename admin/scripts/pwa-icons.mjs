// ---------------------------------------------------------------------------
// The operator panel's home-screen icons (public/icons/*.png), drawn from the
// one picture below.
//
//   node scripts/pwa-icons.mjs
//
// A phone wants PNG files of fixed sizes in the web manifest; the picture is
// an SVG so that it stays one thing to change. The browser that is already on
// the machine turns it into the files (playwright-core, like the e2e scripts):
// no image library is added for three pictures. Run it again only when the
// picture changes, and commit what it writes.
//
// The colours are the design tokens' values (src/styles/tokens.css:
// --brand-500, --neutral-0): a PNG cannot read a CSS variable.
// ---------------------------------------------------------------------------
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

import { launchBrowser } from '../e2e/pages.mjs';

const root = new URL('..', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');
const out = join(root, 'public', 'icons');

const brand = '#2b5fd9';
const paper = '#ffffff';

/**
 * A printer on the brand colour. `inset` is how far the drawing keeps from
 * the edge: a maskable icon is cropped to a circle on some phones, so its
 * drawing stays inside the middle four fifths and its background fills the
 * whole square.
 */
function picture(size, { maskable }) {
  const radius = maskable ? 0 : size * 0.22;
  const scale = maskable ? 0.58 : 0.72;
  const box = size * scale;
  const x = (size - box) / 2;
  const y = (size - box) / 2;
  const u = box / 24;

  return `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 ${size} ${size}">
  <rect width="${size}" height="${size}" rx="${radius}" fill="${brand}"/>
  <g transform="translate(${x} ${y}) scale(${u})" fill="${paper}">
    <rect x="7" y="2.5" width="10" height="5" rx="1"/>
    <rect x="3" y="8.5" width="18" height="9" rx="2"/>
    <rect x="7" y="13.5" width="10" height="8" rx="1" stroke="${brand}" stroke-width="1.2"/>
    <circle cx="17.5" cy="11.5" r="1" fill="${brand}"/>
  </g>
</svg>`;
}

const files = [
  { name: 'icon-192.png', size: 192, maskable: false },
  { name: 'icon-512.png', size: 512, maskable: false },
  { name: 'icon-maskable-512.png', size: 512, maskable: true },
];

mkdirSync(out, { recursive: true });

const browser = await launchBrowser();

try {
  for (const { name, size, maskable } of files) {
    const page = await browser.newPage({ viewport: { width: size, height: size }, deviceScaleFactor: 1 });

    await page.setContent(
      `<html><body style="margin:0;background:transparent">${picture(size, { maskable })}</body></html>`,
    );
    await page.screenshot({ path: join(out, name), omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } });
    await page.close();

    console.log(`wrote public/icons/${name}`);
  }
} finally {
  await browser.close();
}
