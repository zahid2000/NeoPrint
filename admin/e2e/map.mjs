#!/usr/bin/env node
// ---------------------------------------------------------------------------
// The kiosks map on the stack (T-217): OpenFreeMap through MapLibre, on a desk
// and a phone, in the kiosk form as a point picker, and with the map's host
// blocked - a notice, not an empty box. Along the way: the attribution's three
// links are on every map, the page talks to no host but its own and the map's,
// and nothing is written to the console (no policy violation either).
//
//   node e2e/map.mjs                                   # from admin/
//   node e2e/map.mjs ../docs/screenshots/step-7/t217   # + screenshots
// ---------------------------------------------------------------------------
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { launchBrowser } from './pages.mjs';

const base = process.env.ADMIN_URL ?? 'http://localhost:8080';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const mapHost = 'tiles.openfreemap.org';
const shots = process.argv[2] ? resolve(process.argv[2]) : null;

const failures = [];
const check = (ok, text) => {
  console.log(`${ok ? 'ok    ' : 'WRONG '} ${text}`);
  if (!ok) failures.push(text);
};

const browser = await launchBrowser();
const errors = [];
const hosts = new Set();
const ownHost = new URL(base).host;

/** Every console error counts here - a refused request of the policy is one too. */
function watch(page, { blocked = false } = {}) {
  page.on('console', (message) => {
    // A request the check itself blocks is reported by the browser as a failed load; nothing else is excused.
    if (message.type() === 'error' && !(blocked && /Failed to load resource/.test(message.text()))) {
      errors.push(message.text().slice(0, 200));
    }
  });
  page.on('pageerror', (error) => errors.push(String(error).slice(0, 200)));
  page.on('request', (request) => {
    const url = new URL(request.url());
    if (url.protocol.startsWith('http')) hosts.add(url.host);
  });
}

async function shot(page, name) {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  await page.waitForTimeout(600);
  await page.screenshot({ path: join(shots, `${name}.png`) });
  console.log(`       screenshot ${name}.png`);
}

async function signedIn(options, watchOptions) {
  const context = await browser.newContext({ locale: 'az', serviceWorkers: 'block', ...options });
  const page = await context.newPage();
  await page.goto(`${base}/login`, { waitUntil: 'networkidle' });
  await page.locator('input[type=email]').fill('dev-superadmin@neoprint.local');
  await page.locator('input[type=password]').fill(password);
  await page.locator('button[type=submit]').click();
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });
  // Watched from here: the sign-in page's own session probe (a 401 while there
  // is no session yet) is not the map's.
  watch(page, watchOptions);
  return { context, page };
}

async function openMap(page) {
  await page.goto(`${base}/kiosks`, { waitUntil: 'networkidle' });
  await page.locator('mat-button-toggle[value="map"] button').click();
}

/** Drawn: MapLibre's canvas is there, markers are placed and the map has stopped loading. */
async function drawn(page) {
  await page.locator('app-kiosk-map .maplibregl-canvas').waitFor({ timeout: 20_000 });
  await page.locator('app-kiosk-map .map[aria-busy="false"]').waitFor({ timeout: 20_000 });
  await page.waitForLoadState('networkidle');
}

async function attribution(page, where) {
  const links = page.locator('app-kiosk-map [data-map-attribution] a');
  const texts = await links.allInnerTexts();
  check(
    texts.join(' | ') === 'OpenFreeMap | © OpenMapTiles | © OpenStreetMap contributors',
    `${where}: the attribution, all three as links (${texts.join(' | ')})`,
  );
  const visible = await page.locator('app-kiosk-map [data-map-attribution]').isVisible();
  check(visible, `${where}: the attribution is on screen`);
}

try {
  // ---- On a desk -------------------------------------------------------------
  const desk = await signedIn({ viewport: { width: 1366, height: 900 } });
  await openMap(desk.page);
  await drawn(desk.page);
  const markers = await desk.page.locator('.neo-map-marker').count();
  check(markers > 0, `desk: the kiosks are on the map (${markers})`);
  await attribution(desk.page, 'desk');
  await shot(desk.page, '01-admin-map-desktop');

  // A marker's popup names the kiosk.
  await desk.page.locator('.neo-map-marker').last().click();
  check(await desk.page.locator('.maplibregl-popup-content strong').first().isVisible().catch(() => false), 'desk: a marker opens its kiosk');

  // The kiosk form picks a point with a click.
  await desk.page.goto(`${base}/kiosks/new`, { waitUntil: 'networkidle' });
  await drawn(desk.page);
  const canvas = desk.page.locator('app-kiosk-map .maplibregl-canvas');
  const box = await canvas.boundingBox();
  await desk.page.mouse.click(box.x + box.width / 2 + 40, box.y + box.height / 2 + 20);
  await desk.page.waitForTimeout(500);
  const lat = await desk.page.locator('input[type=number]').first().inputValue();
  check(/^\d+\.\d+$/.test(lat), `kiosk form: a click on the map fills the coordinates (${lat})`);
  check((await desk.page.locator('.neo-map-marker').count()) === 1, 'kiosk form: the picked point is shown');
  await attribution(desk.page, 'kiosk form');
  await shot(desk.page, '02-admin-kiosk-form-map');
  await desk.context.close();

  // ---- On a phone -----------------------------------------------------------
  const phone = await signedIn({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  await openMap(phone.page);
  await drawn(phone.page);
  await attribution(phone.page, 'phone');
  const width = await phone.page.evaluate(() => document.documentElement.scrollWidth);
  check(width <= 390, `phone: nothing wider than the screen (${width} px)`);
  await phone.page.locator('app-kiosk-map').scrollIntoViewIfNeeded();
  await shot(phone.page, '03-admin-map-phone');
  await phone.context.close();

  // ---- Who the page talked to ---------------------------------------------
  const others = [...hosts].filter((host) => host !== ownHost && host !== mapHost && !host.startsWith('localhost'));
  check(hosts.has(mapHost), `the map came from ${mapHost}`);
  check(others.length === 0, `no other host was asked (${others.join(', ') || 'none'})`);
  check(errors.length === 0, `console: no error (${errors.length})${errors.length ? ': ' + errors.join(' / ') : ''}`);

  // ---- The map's host unreachable ------------------------------------------
  const blocked = await signedIn({ viewport: { width: 1366, height: 900 } }, { blocked: true });
  await blocked.context.route(`https://${mapHost}/**`, (route) => route.abort('connectionrefused'));
  await openMap(blocked.page);
  const notice = blocked.page.locator('app-kiosk-map .map__failed');
  await notice.waitFor({ timeout: 15_000 });
  const said = await notice.innerText();
  check(said.includes('Xəritə yüklənmədi') && said.includes('Kiosklar siyahıda görünür'), 'blocked: a notice says what happened and where the kiosks are');
  check(!(await blocked.page.locator('app-kiosk-map .map').isVisible()), 'blocked: no empty map box');
  await shot(blocked.page, '04-admin-map-blocked');

  // Retry: still blocked, still the notice; unblocked, the map.
  await notice.locator('button').click();
  await notice.waitFor({ state: 'detached', timeout: 5_000 }).catch(() => undefined);
  await notice.waitFor({ timeout: 15_000 });
  check(true, 'blocked: "Yenidən cəhd et" tries again and says so again');
  await blocked.context.unroute(`https://${mapHost}/**`);
  await notice.locator('button').click();
  await drawn(blocked.page);
  check(await blocked.page.locator('app-kiosk-map .maplibregl-canvas').isVisible(), 'blocked, then reachable: "Yenidən cəhd et" draws the map');

  // The kiosk form says to type the coordinates.
  await blocked.context.route(`https://${mapHost}/**`, (route) => route.abort('connectionrefused'));
  await blocked.page.goto(`${base}/kiosks/new`, { waitUntil: 'networkidle' });
  await blocked.page.locator('app-kiosk-map .map__failed').waitFor({ timeout: 15_000 });
  check(
    (await blocked.page.locator('app-kiosk-map .map__failed').innerText()).includes('əl ilə yazın'),
    'blocked, kiosk form: asks for the coordinates by hand',
  );
  await blocked.context.close();
  check(errors.length === 0, `console with the host blocked: nothing but the blocked loads (${errors.length})`);
} finally {
  await browser.close();
}

console.log(failures.length ? `\n${failures.length} check(s) failed` : '\nall checks passed');
process.exit(failures.length ? 1 : 0);
