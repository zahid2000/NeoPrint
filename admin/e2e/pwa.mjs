// ---------------------------------------------------------------------------
// The operator panel as an operator has it: on a phone-sized screen, installed
// from the development stack, with the connection going away (T-211).
//
//   docker compose up -d                       # the stack and a running kiosk
//   node e2e/pwa.mjs                           # from admin/
//   node e2e/pwa.mjs ../docs/screenshots/step-7/m7
//
// It checks, in this order:
//
//   1. what the proxy says about caching - the service worker, its manifest of
//      the build and the web manifest must never be kept by the browser, the
//      hashed files always;
//   2. that an operator who signs in lands in the operator panel, and that
//      each of its pages opens at a phone's width without scrolling sideways,
//      without a raw key and without an error;
//   3. that the service worker takes the page over, and that with the network
//      gone the open panel keeps showing what it last loaded, says how old it
//      is and switches its buttons off - and comes back by itself;
//   4. that a reload without a network still opens the panel's shell from the
//      device. The session itself cannot be restored without the server - the
//      access token lives in memory only - so what opens then is the sign-in
//      page, not a browser error.
//
// This is the one script that lets the service worker run; the others block
// it (see pages.mjs). Needs Chrome or Edge on the machine.
// ---------------------------------------------------------------------------
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { findRawKeys, launchBrowser } from './pages.mjs';

const base = process.env.ADMIN_URL ?? 'http://localhost:8080';
const email = process.env.NEOPRINT_DEV_OPERATOR_EMAIL ?? 'dev-operator@neoprint.local';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const shots = process.argv[2] ? resolve(process.argv[2]) : null;

const failures = [];
const check = (ok, text) => {
  console.log(`${ok ? 'ok    ' : 'WRONG '} ${text}`);
  if (!ok) failures.push(text);
};

// --- 1. what may be kept ------------------------------------------------------
async function header(path, name) {
  const response = await fetch(`${base}${path}`);

  return { status: response.status, value: response.headers.get(name) ?? '', body: await response.text() };
}

const worker = await header('/ngsw-worker.js', 'cache-control');
check(worker.status === 200 && worker.value.includes('no-store'), `/ngsw-worker.js is never kept: "${worker.value}"`);

const build = await header('/ngsw.json', 'cache-control');
check(build.status === 200 && build.value.includes('no-store'), `/ngsw.json is never kept: "${build.value}"`);

const manifest = await header('/manifest.webmanifest', 'content-type');
const declared = manifest.status === 200 ? JSON.parse(manifest.body) : {};
check(manifest.value.includes('application/manifest+json'), `/manifest.webmanifest is served as a manifest: "${manifest.value}"`);
check(declared.start_url === '/operator' && declared.display === 'standalone', `it opens ${declared.start_url} as an app of its own (${declared.display})`);

for (const icon of declared.icons ?? []) {
  const picture = await fetch(`${base}/${icon.src}`);
  check(picture.ok && (picture.headers.get('content-type') ?? '').includes('image/png'), `icon ${icon.src} (${icon.sizes}, ${icon.purpose}) is there`);
}

const hashed = Object.keys(JSON.parse(build.body).hashTable ?? {}).find((file) => /^\/main-.*\.js$/.test(file));
const script = hashed ? await header(hashed, 'cache-control') : { value: '' };
check(script.value.includes('immutable'), `a hashed file is kept for good: ${hashed} "${script.value}"`);

// --- 2. the pages on a phone ----------------------------------------------------
const browser = await launchBrowser();
const context = await browser.newContext({ viewport: { width: 390, height: 844 }, locale: 'az', isMobile: true, hasTouch: true });
const page = await context.newPage();
const consoleErrors = [];

page.on('console', (message) => {
  if (message.type() === 'error' && !/Failed to load resource|net::ERR_/.test(message.text())) consoleErrors.push(message.text().slice(0, 160));
});

async function shot(name) {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  await page.screenshot({ path: join(shots, `${name}.png`) });
}

async function looksRight(name) {
  const state = await page.evaluate(() => ({
    heading: (document.querySelector('h1')?.textContent ?? '').replace(/\s+/g, ' ').trim(),
    sideways: document.documentElement.scrollWidth - window.innerWidth,
    banner: !!document.querySelector('app-error-banner'),
    skeletons: document.querySelectorAll('.neo-skeleton').length,
  }));
  const keys = await page.evaluate(findRawKeys);

  check(
    !!state.heading && state.sideways <= 1 && !state.banner && state.skeletons === 0 && keys.length === 0,
    `${name}: "${state.heading}"${state.sideways > 1 ? `, scrolls sideways by ${state.sideways}px` : ''}${state.banner ? ', error banner' : ''}${state.skeletons ? ', still loading' : ''}${keys.length ? `, raw keys ${keys.join(', ')}` : ''}`,
  );
}

try {
  await page.goto(`${base}/login`, { waitUntil: 'networkidle' });
  await page.locator('input[type=email]').fill(email);
  await page.locator('input[type=password]').fill(password);
  await page.locator('button[type=submit]').click();
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });
  await page.waitForLoadState('networkidle');

  check(new URL(page.url()).pathname === '/operator', `an operator lands in the operator panel after signing in: ${new URL(page.url()).pathname}`);
  await page.waitForTimeout(800);
  await looksRight('Mənim kiosklarım');
  await shot('01-kiosks');

  const cards = await page.locator('article[data-kiosk]').count();
  check(cards > 0, `${cards} kiosk card(s)`);

  // A kiosk's own page, from its card.
  await page.locator('article[data-kiosk] a').first().click();
  await page.waitForURL(/\/operator\/kiosks\//);
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(600);
  await looksRight('Kiosk');
  await shot('02-kiosk');

  // "Kağız doldur" opens as a dialog that fits the screen.
  const refill = page.locator('button.op-primary').first();
  if (await refill.isVisible()) {
    await refill.click();
    await page.locator('mat-dialog-container').waitFor({ timeout: 10_000 });
    await page.waitForTimeout(400);
    const box = await page.locator('mat-dialog-container').boundingBox();
    check(!!box && box.x >= 0 && box.x + box.width <= 390, `the refill dialog fits the screen: ${Math.round(box?.width ?? 0)}px wide`);
    await shot('03-refill-dialog');
    await page.keyboard.press('Escape');
    await page.locator('mat-dialog-container').waitFor({ state: 'detached', timeout: 10_000 });
  }

  for (const [path, name, file] of [
    ['tasks', 'Tapşırıqlar', '04-tasks'],
    ['incidents', 'Incident-lər', '05-incidents'],
    ['inventory', 'Anbar', '06-inventory'],
  ]) {
    await page.locator(`nav a[href="/operator/${path}"]`).click();
    await page.waitForURL(new RegExp(`/operator/${path}$`));
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(600);
    await looksRight(name);
    await shot(file);
  }

  // --- 3. the service worker, and the network going away ---------------------------
  const controlled = await page
    .evaluate(async () => {
      const registration = await Promise.race([
        navigator.serviceWorker.ready,
        new Promise((resolve) => setTimeout(() => resolve(null), 45_000)),
      ]);

      return registration ? (registration.active?.scriptURL ?? '') : '';
    })
    .catch(() => '');
  check(controlled.endsWith('/ngsw-worker.js'), `the service worker is active: ${controlled || '(none within 45s)'}`);

  await page.locator('nav a[href="/operator"]').click();
  await page.waitForURL(/\/operator$/);
  await page.waitForLoadState('networkidle');

  await context.setOffline(true);

  // The panel notices by asking: at the latest with the thirty-second poll of a lost connection.
  const noticed = await page
    .locator('app-operator-freshness .neo-notice')
    .waitFor({ timeout: 75_000 })
    .then(() => true, () => false);
  const stillThere = await page.locator('article[data-kiosk]').count();
  const disabled = await page.locator('article[data-kiosk] button.op-primary').evaluateAll((buttons) => buttons.every((button) => button.disabled));

  check(noticed, 'without a network the page says the data is old');
  check(stillThere === cards, `the ${stillThere} card(s) are still there to read`);
  check(disabled, 'and its buttons are switched off');
  await shot('07-offline');

  await context.setOffline(false);
  const back = await page
    .locator('app-operator-freshness .neo-notice')
    .waitFor({ state: 'detached', timeout: 75_000 })
    .then(() => true, () => false);
  check(back, 'with the network back the notice goes by itself');

  // --- 4. a reload without a network -------------------------------------------------
  await context.setOffline(true);
  const reloaded = await page.reload({ waitUntil: 'domcontentloaded', timeout: 30_000 }).then(() => true, () => false);
  await page.waitForTimeout(2500);
  const shell = await page.evaluate(() => !!document.querySelector('app-root') && document.body.innerText.trim().length > 0).catch(() => false);
  check(reloaded && shell, `a reload without a network opens the panel from the device (${new URL(page.url()).pathname})`);
  await shot('08-reload-offline');
  await context.setOffline(false);
} finally {
  await browser.close();
}

console.log(`\nconsole errors: ${consoleErrors.length}`);
consoleErrors.forEach((line) => console.log(`  ${line}`));
console.log(failures.length === 0 ? '\nThe operator panel works on a phone, installed and with the network gone.' : `\n${failures.length} thing(s) wrong.`);
process.exit(failures.length === 0 ? 0 : 1);
