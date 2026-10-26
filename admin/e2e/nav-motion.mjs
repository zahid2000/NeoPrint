// ---------------------------------------------------------------------------
// The side menu's motion (T-328, docs/DESIGN_SYSTEM.md 5.3) on the development
// stack: a group opening and shutting (desktop), the icon strip opening into
// the full menu (tablet), the drawer (phone). For each, the size is sampled on
// every frame - it passes through the sizes in between, and ends where it
// should - and a few frames are saved as pictures. Also: nothing moves on the
// first load; with reduced motion nothing moves at all; the keyboard does not
// reach a shut group and reaches an opened one at once; no console error.
//
//   node e2e/nav-motion.mjs ../docs/screenshots/step-7b/t328      # from admin/
//
// Needs Chrome or Edge.
// ---------------------------------------------------------------------------
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { launchBrowser } from './pages.mjs';

const base = process.env.ADMIN_URL ?? 'http://localhost:8080';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const shots = process.argv[2] ? resolve(process.argv[2]) : null;

const failures = [];
const check = (ok, text) => {
  console.log(`${ok ? 'ok    ' : 'WRONG '} ${text}`);
  if (!ok) failures.push(text);
};


/** Samples one number on every frame for a while, in the page. */
function sampler() {
  window.__sample = (read, ms) =>
    new Promise((done) => {
      const values = [];
      const start = performance.now();
      const tick = () => {
        values.push(Math.round(read() * 10) / 10);
        if (performance.now() - start < ms) requestAnimationFrame(tick);
        else done(values);
      };
      tick();
    });
}

const browser = await launchBrowser();
const consoleErrors = [];

async function screen(name, viewport, options = {}) {
  const context = await browser.newContext({ viewport, serviceWorkers: 'block', ...options });
  await context.addInitScript(sampler);
  const page = await context.newPage();
  page.on('console', (message) => message.type() === 'error' && consoleErrors.push(`${name}: ${message.text()}`));
  page.on('pageerror', (error) => consoleErrors.push(`${name}: ${error.message}`));
  // Four sign-ins in all: the limit is ten a minute per address (T-306).
  await page.goto(`${base}/login`, { waitUntil: 'networkidle' });
  await page.fill('input[type="email"]', 'dev-superadmin@neoprint.local');
  await page.fill('input[type="password"]', password);
  await page.click('button[type="submit"]');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });
  return { context, page };
}

const frames = async (page, name, act, clip) => {
  if (shots) mkdirSync(shots, { recursive: true });
  await act();
  for (const [index, wait] of [0, 70, 140, 400].entries()) {
    if (index > 0) await page.waitForTimeout(wait - [0, 70, 140, 400][index - 1]);
    if (shots) await page.screenshot({ path: join(shots, `${name}-${index + 1}.png`), clip });
  }
};

const between = (values, from, to) => values.some((value) => value > Math.min(from, to) + 1 && value < Math.max(from, to) - 1);

try {
  // ---- Desktop: a group opens and shuts by its height ----
  {
    const { context, page } = await screen('desktop', { width: 1366, height: 900 });
    // The first load: the page's own group is drawn open, and stays the height it was drawn.
    await page.goto(`${base}/kiosks`, { waitUntil: 'domcontentloaded' });
    await page.waitForSelector('.shell__group-body--open');
    const first = await page.evaluate(() => window.__sample(() => document.querySelector('.shell__group-body--open').getBoundingClientRect().height, 600));
    check(new Set(first).size === 1 && first[0] > 0, `first load: the open group is drawn at its height and does not grow (${first[0]}px, ${new Set(first).size} size(s))`);
    await page.waitForTimeout(400);
    check(await page.evaluate(() => document.querySelector('.shell').classList.contains('mat-drawer-transition')), 'the motion is switched on after the first paint');

    const heading = page.locator('[data-nav-group="finance"]');
    const body = page.locator('#nav-group-finance');
    const sample = () => page.evaluate(() => window.__sample(() => document.querySelector('#nav-group-finance').getBoundingClientRect().height, 450));

    // The keyboard does not reach a shut group: from its heading, Tab goes past it.
    await heading.focus();
    await page.keyboard.press('Tab');
    check(!(await page.evaluate(() => document.activeElement?.closest('#nav-group-finance'))), 'Tab passes a shut group by');
    check((await body.getAttribute('inert')) !== null, 'a shut group is inert');

    await heading.focus();
    const opening = sample();
    await page.keyboard.press('Enter');
    const opened = await opening;
    const full = opened.at(-1);
    check(full > 0 && between(opened, 0, full), `the group opens through the heights in between (0 → ${full}px, ${opened.length} frames)`);
    await page.keyboard.press('Tab');
    check(await page.evaluate(() => !!document.activeElement?.closest('#nav-group-finance a.shell__nav-item')), 'and Tab reaches its first page at once');
    check((await heading.getAttribute('aria-expanded')) === 'true' && (await heading.getAttribute('aria-controls')) === 'nav-group-finance', 'the heading says it is open and what it opens');

    const shutting = sample();
    await heading.click();
    const shut = await shutting;
    check(shut.at(-1) === 0 && between(shut, 0, full), `and shuts the same way (${full} → 0px)`);

    const clip = { x: 0, y: 0, width: 280, height: 900 };
    await frames(page, 'desktop-group', () => heading.click(), clip);
    await context.close();
  }

  // ---- Tablet: the icon strip opens into the full menu ----
  {
    const { context, page } = await screen('tablet', { width: 820, height: 1180 });
    await page.goto(`${base}/kiosks`, { waitUntil: 'networkidle' });
    const width = () => page.evaluate(() => window.__sample(() => document.querySelector('.shell__sidenav').getBoundingClientRect().width, 450));
    const narrow = await page.evaluate(() => document.querySelector('.shell__sidenav').getBoundingClientRect().width);
    const opening = width();
    await page.getByRole('button', { name: 'Menyunu aç və ya bağla' }).click();
    const margin = page.evaluate(() => window.__sample(() => parseFloat(getComputedStyle(document.querySelector('.mat-drawer-content')).marginLeft), 450));
    const opened = await opening;
    check(between(opened, narrow, opened.at(-1)) && opened.at(-1) > narrow, `the strip widens through the widths in between (${narrow} → ${opened.at(-1)}px)`);
    check(new Set(await margin).size === 1, 'the page stays where it is while the menu opens over it');
    await page.waitForTimeout(300);
    const open = await page.evaluate(() => ({
      width: document.querySelector('.shell__sidenav').getBoundingClientRect().width,
      labels: document.querySelectorAll('.shell__nav-label').length,
    }));
    check(open.width > narrow && open.labels > 0, `and stays open with the names (${open.width}px, ${open.labels} names)`);
    const folding = page.evaluate(() => window.__sample(() => parseFloat(getComputedStyle(document.querySelector('.mat-drawer-content')).marginLeft), 600));
    await page.keyboard.press('Escape');
    const folded = await folding;
    check(new Set(folded).size === 1 && folded[0] === narrow, `Escape folds it back, the page keeps the strip's room (${[...new Set(folded)].join(', ')}px)`);
    check((await page.evaluate(() => document.querySelector('.shell__sidenav').getBoundingClientRect().width)) === narrow, 'into the icons');
    await frames(page, 'tablet-rail', () => page.getByRole('button', { name: 'Menyunu aç və ya bağla' }).click(), { x: 0, y: 0, width: 820, height: 700 });
    await context.close();
  }

  // ---- Phone: the drawer slides in ----
  {
    const { context, page } = await screen('phone', { width: 390, height: 844 });
    await page.goto(`${base}/kiosks`, { waitUntil: 'networkidle' });
    const left = () => page.evaluate(() => window.__sample(() => document.querySelector('.shell__sidenav').getBoundingClientRect().left, 450));
    const opening = left();
    await page.getByRole('button', { name: 'Menyunu aç və ya bağla' }).click();
    const opened = await opening;
    check(opened.at(-1) === 0 && between(opened, opened[0], 0) && opened[0] < 0, `the drawer slides in (${opened[0]} → 0px)`);
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    check(overflow <= 0, `no sideways scroll (${overflow}px)`);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(500);
    await frames(page, 'phone-drawer', () => page.getByRole('button', { name: 'Menyunu aç və ya bağla' }).click(), { x: 0, y: 0, width: 390, height: 844 });
    await context.close();
  }

  // ---- Reduced motion: everything at once ----
  {
    const { context, page } = await screen('reduced', { width: 1366, height: 900 }, { reducedMotion: 'reduce' });
    await page.goto(`${base}/kiosks`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(400);
    check(!(await page.evaluate(() => document.querySelector('.shell').classList.contains('mat-drawer-transition'))), 'reduced motion: the motion is never switched on');
    const opening = page.evaluate(() => window.__sample(() => document.querySelector('#nav-group-finance').getBoundingClientRect().height, 300));
    await page.locator('[data-nav-group="finance"]').click();
    const opened = await opening;
    const full = opened.at(-1);
    check(full > 0 && !between(opened, 0, full), `reduced motion: the group is open on the next frame (${opened.filter((v) => v > 0 && v < full).length} frames in between)`);
    await context.close();
  }
} finally {
  await browser.close();
}

check(consoleErrors.length === 0, `no console error${consoleErrors.length ? `: ${consoleErrors.join(' | ')}` : ''}`);
console.log(failures.length ? `\n${failures.length} check(s) failed.` : '\nEvery check passed.');
process.exit(failures.length ? 1 : 0);
