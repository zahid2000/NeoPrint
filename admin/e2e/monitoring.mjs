// ---------------------------------------------------------------------------
// The kiosk and printer pages on the development stack (T-212): a kiosk's
// "Cari vəziyyət" follows its reports live, "Əmrlər" pauses and resumes it and
// shows the kiosk's answers live, the printer page and the printers page show
// what the printer reported - on a desktop and on a phone, with no console
// error, no raw key, no page left loading and no sideways scroll. Then an
// account that looks after one kiosk sees that kiosk's printers only.
//
//   docker compose up -d && docker start neoprint-kshot   # the stack and KSK-9002
//   node e2e/monitoring.mjs                               # from admin/
//   node e2e/monitoring.mjs ../docs/screenshots/step-7/t212
//
// Pauses KSK-9002 and resumes it; the scoped account it makes is switched off
// at the end. Needs Chrome or Edge on the machine.
// ---------------------------------------------------------------------------
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { findRawKeys, launchBrowser } from './pages.mjs';

const base = process.env.ADMIN_URL ?? 'http://localhost:8080';
const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const shots = process.argv[2] ? resolve(process.argv[2]) : null;
const code = 'KSK-9002';

const failures = [];
const check = (ok, text) => {
  console.log(`${ok ? 'ok    ' : 'WRONG '} ${text}`);
  if (!ok) failures.push(text);
};

async function signIn(email, secret = password) {
  const response = await fetch(`${api}/api/auth/admin/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password: secret }),
  });
  if (!response.ok) throw new Error(`sign-in ${email}: ${response.status}`);
  return (await response.json()).accessToken;
}

const root = await signIn('dev-superadmin@neoprint.local');

async function call(token, method, path, body) {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'Idempotency-Key': randomUUID() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${(await response.text()).slice(0, 200)}`);
  return response.status === 204 ? null : response.json().catch(() => null);
}

const kiosks = await call(root, 'GET', '/api/admin/kiosks');
const kiosk = kiosks.find((item) => item.code === code);
const other = kiosks.find((item) => item.code !== code && item.activeDeviceId);
if (!kiosk) throw new Error(`${code} is not on the stack.`);
const printer = (await call(root, 'GET', `/api/admin/kiosks/${kiosk.id}/printers/telemetry`))[0]?.sample.printer.printerName;
if (!printer) throw new Error(`${code} has reported no printer: is neoprint-kshot running?`);

const browser = await launchBrowser();

/** Opens a signed-in tab at the given width and notes what goes wrong in it. */
async function tab(email, secret, width) {
  const context = await browser.newContext({
    viewport: { width, height: width < 600 ? 844 : 900 },
    locale: 'az',
    isMobile: width < 600,
    hasTouch: width < 600,
    serviceWorkers: 'block',
  });
  const page = await context.newPage();
  const errors = [];
  page.on('console', (message) => {
    if (message.type() === 'error' && !/Failed to load resource/.test(message.text())) errors.push(message.text().slice(0, 200));
  });
  page.on('pageerror', (error) => errors.push(String(error).slice(0, 200)));
  page.on('response', (response) => {
    const url = new URL(response.url());
    if (response.status() >= 400 && url.pathname.startsWith('/api/') && !(response.status() === 401 && url.pathname.endsWith('/refresh'))) {
      console.log(`       (${response.status()} ${url.pathname})`);
    }
  });

  await page.goto(`${base}/login`, { waitUntil: 'networkidle' });
  await page.locator('input[type=email]').fill(email);
  await page.locator('input[type=password]').fill(secret);
  await page.locator('button[type=submit]').click();
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });

  return { page, errors, context };
}

/** Nothing left loading, no raw key, no error banner, no sideways scroll. */
async function clean(page, what) {
  await page.waitForLoadState('networkidle').catch(() => undefined);
  // A full reload restores the session first: the page is not there until its heading is.
  await page.locator('main h1').first().waitFor({ timeout: 15_000 }).catch(() => undefined);
  await page.waitForTimeout(800);
  const state = await page.evaluate(() => ({
    heading: !!document.querySelector('main h1'),
    skeletons: document.querySelectorAll('.neo-skeleton').length,
    banners: document.querySelectorAll('app-error-banner').length,
    sideways: document.documentElement.scrollWidth > document.documentElement.clientWidth + 1,
  }));
  const raw = await page.evaluate(findRawKeys);
  check(state.heading && state.skeletons === 0 && state.banners === 0 && raw.length === 0 && !state.sideways,
    `${what}: shown with its heading, loaded, no error, no raw key${raw.length ? ` (${raw.join(', ')})` : ''}, no sideways scroll`);
}

async function shot(page, name) {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  await page.waitForTimeout(400);
  // The repository is public: what names a machine or a network is hidden before the picture is taken.
  await page.evaluate(() => {
    const private_ = /^(Kompüterin adı|Lokal IP|MAC|Seriya nömrəsi|Ünvan \(URI\)|Əməliyyat sistemi|Computer name|Local IP|Serial number|Address \(URI\)|Operating system)$/;
    for (const row of document.querySelectorAll('.neo-metric')) {
      const label = row.querySelector('dt')?.textContent?.trim() ?? '';
      const value = row.querySelector('dd');
      if (value && private_.test(label) && !value.querySelector('.neo-caption')) value.textContent = '•••';
    }
  });
  await page.screenshot({ path: join(shots, `${name}.png`), fullPage: true });
}

const openTab = async (page, label) => {
  await page.getByRole('tab', { name: label, exact: true }).click();
  await page.waitForTimeout(600);
};

let account = null;

try {
  // --- the desktop -----------------------------------------------------------
  const desk = await tab('dev-superadmin@neoprint.local', password, 1366);
  const { page } = desk;

  await page.goto(`${base}/kiosks/${kiosk.id}`, { waitUntil: 'networkidle' });
  await openTab(page, 'Cari vəziyyət');
  const reported = page.locator('.status__at');
  await reported.waitFor({ timeout: 15_000 });
  const first = await reported.innerText();
  check((await page.locator('.status__health app-status-chip').count()) === 5, 'five health chips: disk, memory, latency, connection, clock');
  check((await page.locator('.status__group').count()) === 6, 'six groups of plan 7.9');
  check((await page.locator('app-line-chart canvas').count()) >= 1, 'the graphs are drawn');
  await clean(page, 'Cari vəziyyət');
  await shot(page, '01-status-desktop');

  // The kiosk reports every 30 seconds: the time changes without a reload.
  await page.waitForFunction((before) => document.querySelector('.status__at')?.textContent?.trim() !== before.trim(), first, { timeout: 45_000 })
    .then(() => check(true, `the next report arrives live (${first.trim()} → later)`))
    .catch(() => check(false, 'the next report did not arrive within 45 s'));

  await openTab(page, 'Əmrlər');
  await page.getByRole('button', { name: 'Dayandır' }).click();
  await page.locator('mat-dialog-container').getByRole('button', { name: 'Dayandır' }).click();
  await page.getByRole('button', { name: 'Davam et' }).waitFor({ timeout: 15_000 });
  check(true, 'paused: the same place now says "Davam et"');
  await page.locator('tr.mat-mdc-row', { hasText: 'Fasilə' }).first().filter({ hasText: 'Uğurlu' })
    .waitFor({ timeout: 45_000 })
    .then(() => check(true, 'the kiosk answered the pause: the row says "Uğurlu" without a reload'))
    .catch(() => check(false, 'the pause row did not turn "Uğurlu"'));
  await shot(page, '02-commands-paused');
  await page.getByRole('button', { name: 'Davam et' }).click();
  await page.getByRole('button', { name: 'Dayandır' }).waitFor({ timeout: 15_000 });
  await page.locator('tr.mat-mdc-row', { hasText: 'Davam et' }).first().filter({ hasText: 'Uğurlu' })
    .waitFor({ timeout: 45_000 })
    .then(() => check(true, 'resumed, and the kiosk answered'))
    .catch(() => check(false, 'the resume row did not turn "Uğurlu"'));
  await clean(page, 'Əmrlər');
  await shot(page, '03-commands');

  for (const [label, name] of [['Sərf', '04-supplies'], ['Servis tapşırıqları', '05-tasks'], ['Incident-lər', '06-incidents']]) {
    await openTab(page, label);
    await clean(page, label);
    await shot(page, name);
  }

  await openTab(page, 'Printerlər');
  check((await page.locator('app-kiosk-printers table').count()) === 1, 'the printers tab keeps one table; the commands are in "Əmrlər"');
  await page.locator('app-kiosk-printers td a').first().click();
  await page.waitForURL(/\/printers\//);
  await page.locator('app-page-header').waitFor();
  check((await page.locator('h1').innerText()).includes(printer), 'the printer opens on its own page');
  check((await page.innerText('body')).includes('Hazırdır') || (await page.innerText('body')).includes('Çap edir'), 'its status chip says how it is');
  await clean(page, 'Printer detalı');
  await shot(page, '07-printer-desktop');

  await page.goto(`${base}/printers`, { waitUntil: 'networkidle' });
  await page.locator('tr.mat-mdc-row').first().waitFor({ timeout: 15_000 }).catch(() => undefined);
  const rows = await page.locator('tr.mat-mdc-row').count();
  check(rows >= 1, `the printers page lists ${rows} printer(s)`);
  await clean(page, 'Printerlər');
  await shot(page, '08-printers-desktop');
  await page.getByRole('radio', { name: /Əlçatmaz/ }).click().catch(() => page.getByText(/Əlçatmaz \(/).click());
  await page.waitForTimeout(400);
  await shot(page, '09-printers-filter');
  check(desk.errors.length === 0, `desktop: no console error${desk.errors.length ? `: ${desk.errors.join(' | ')}` : ''}`);
  await desk.context.close();

  // --- a phone ---------------------------------------------------------------
  const phone = await tab('dev-superadmin@neoprint.local', password, 390);

  // The menu does not take the page's room on a phone: shut, opened over the page, shut again by a choice.
  const menu = phone.page.locator('mat-sidenav');
  const menuOpen = async () => (await menu.getAttribute('class'))?.includes('mat-drawer-opened') ?? false;
  await phone.page.waitForTimeout(600);
  check(!(await menuOpen()), 'phone: the menu starts shut');
  await phone.page.getByRole('button', { name: /menyu/i }).click();
  await phone.page.waitForTimeout(500);
  check(await menuOpen(), 'phone: ☰ opens the menu over the page');
  await shot(phone.page, '14-menu-phone');
  await menu.getByRole('link', { name: 'Printerlər' }).click();
  await phone.page.waitForURL(/\/printers$/);
  await phone.page.waitForTimeout(600);
  check(!(await menuOpen()), 'phone: choosing a page shuts the menu');

  await phone.page.goto(`${base}/kiosks/${kiosk.id}`, { waitUntil: 'networkidle' });
  await openTab(phone.page, 'Cari vəziyyət');
  await phone.page.locator('.status__at').waitFor({ timeout: 15_000 });
  await clean(phone.page, 'Cari vəziyyət, 390 px');
  await shot(phone.page, '10-status-phone');
  await phone.page.goto(`${base}/kiosks/${kiosk.id}/printers/${encodeURIComponent(printer)}`, { waitUntil: 'networkidle' });
  await phone.page.locator('app-page-header').waitFor();
  await clean(phone.page, 'Printer detalı, 390 px');
  await shot(phone.page, '11-printer-phone');
  await phone.page.goto(`${base}/printers`, { waitUntil: 'networkidle' });
  await clean(phone.page, 'Printerlər, 390 px');
  await shot(phone.page, '12-printers-phone');
  check(phone.errors.length === 0, `phone: no console error${phone.errors.length ? `: ${phone.errors.join(' | ')}` : ''}`);
  await phone.context.close();

  // --- an account that looks after one kiosk ----------------------------------
  const stamp = new Date().toISOString().replace(/\D/g, '').slice(0, 14);
  const role = await call(root, 'POST', '/api/admin/roles', {
    name: `Printers check ${stamp}`,
    description: 'Made by admin/e2e/monitoring.mjs; safe to delete.',
    permissions: ['kiosks.view', 'printers.view', 'supplies.view', 'incidents.view', 'service.tasks.view'],
  });
  const firstSecret = `M1-${randomBytes(9).toString('base64url')}!a`;
  const secret = `M2-${randomBytes(9).toString('base64url')}!a`;
  const email = `printers-check-${stamp}@neoprint.local`;
  account = await call(root, 'POST', '/api/admin/admins', {
    email,
    fullName: 'Printers check',
    password: firstSecret,
    roleIds: [role.id],
    allKiosks: false,
    kioskIds: [kiosk.id],
  });
  await call(await signIn(email, firstSecret), 'POST', '/api/auth/admin/change-password', { currentPassword: firstSecret, newPassword: secret });

  const scoped = await tab(email, secret, 1366);
  await scoped.page.goto(`${base}/printers`, { waitUntil: 'networkidle' });
  await scoped.page.locator('tr.mat-mdc-row').first().waitFor({ timeout: 15_000 }).catch(() => undefined);
  const kiosksShown = await scoped.page.locator('tr.mat-mdc-row td:first-child').allInnerTexts();
  check(kiosksShown.length > 0 && kiosksShown.every((text) => text.trim() === code), `scoped: the printers page shows ${code} only (${[...new Set(kiosksShown.map((text) => text.trim()))].join(', ')})`);
  check(!(await scoped.page.getByRole('button', { name: 'Dayandır' }).count()), 'scoped: no command it may not send');
  if (other) {
    const answer = await fetch(`${api}/api/admin/kiosks/${other.id}/telemetry`, { headers: { Authorization: `Bearer ${await signIn(email, secret)}` } });
    check(answer.status === 404, `scoped: another kiosk's telemetry is 404 (${answer.status})`);
  }
  await shot(scoped.page, '13-printers-scoped');
  check(scoped.errors.length === 0, `scoped: no console error${scoped.errors.length ? `: ${scoped.errors.join(' | ')}` : ''}`);
} finally {
  await browser.close();
  if (account) {
    await call(root, 'POST', `/api/admin/admins/${account.id}/active`, { isActive: false, reason: 'monitoring check finished' }).catch(() => undefined);
  }
  // Whatever happened, KSK-9002 is not left paused.
  await call(root, 'POST', `/api/admin/kiosks/${kiosk.id}/resume`).catch(() => undefined);
}

console.log(failures.length === 0 ? '\nEverything as it should be.' : `\n${failures.length} problem(s).`);
process.exit(failures.length === 0 ? 0 : 1);
