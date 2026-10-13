// ---------------------------------------------------------------------------
// "Kiosk aktivləşdir" in the operator panel, on the development stack, at a
// phone's width (T-265): a machine registers and shows its code, the operator
// types the code, sees the machine, binds it to a kiosk that waits for one, and
// lands on that kiosk's page - each step checked against what the server holds.
//
//   docker compose up -d
//   node e2e/activate.mjs                                  # from admin/
//   node e2e/activate.mjs ../docs/screenshots/step-7/t265  # and keep the pictures
//
// It brings its own data and takes it away (T-266): the kiosk it binds to is
// its own ("E2E aktivləşdirmə"), made at the start and removed at the end with
// both machines and everything written about them (e2e/dev-data.mjs). The
// machines' private keys are thrown away: they are nobody's.
//
// Reading the QR with the camera is not covered: no desktop browser on Windows
// can read codes, so the panel offers no camera here (docs/MANUAL_TESTS.md MT-09).
//
// Needs Chrome or Edge on the machine (playwright-core brings no browser).
// ---------------------------------------------------------------------------
import { randomUUID } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { makeKiosk, purgeKiosk, registerMachine } from './dev-data.mjs';
import { launchBrowser } from './pages.mjs';

const base = process.env.ADMIN_URL ?? 'http://localhost:8080';
const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const shots = process.argv[2] ? resolve(process.argv[2]) : null;
const failures = [];
const check = (ok, text) => {
  console.log(`${ok ? 'ok    ' : 'WRONG '} ${text}`);
  if (!ok) failures.push(text);
};

async function signIn(email) {
  const response = await fetch(`${api}/api/auth/admin/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  });
  if (!response.ok) throw new Error(`sign-in ${email}: ${response.status}`);
  return (await response.json()).accessToken;
}

const root = await signIn('dev-superadmin@neoprint.local');

async function call(method, path, body, anonymous = false) {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: {
      ...(anonymous ? {} : { Authorization: `Bearer ${root}` }),
      'Content-Type': 'application/json',
      'Idempotency-Key': randomUUID(),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${(await response.text()).slice(0, 200)}`);
  return response.status === 204 ? null : response.json().catch(() => null);
}

// A kiosk of the check's own that has lost its machine, and a new machine showing its code.
const kiosk = await makeKiosk(call, 'E2E aktivləşdirmə');
let machine = null;

/** Takes away everything the check made, whatever happened in between. */
const takeAway = () => purgeKiosk(api, kiosk.id, [kiosk.deviceId, ...(machine ? [machine.deviceId] : [])]);

try {
  await call('POST', `/api/admin/devices/${kiosk.deviceId}/revoke`, { reason: 'E2E: the kiosk waits for a machine' });
  machine = await registerMachine(call, `e2e-activate-${Date.now() % 100000}`);
} catch (error) {
  takeAway();
  throw error;
}

console.log(`kiosk ${kiosk.code} waits for a machine; the new machine shows a code`);

const browser = await launchBrowser();
const context = await browser.newContext({
  viewport: { width: 390, height: 844 },
  locale: 'az',
  isMobile: true,
  hasTouch: true,
  serviceWorkers: 'block',
});
const page = await context.newPage();
const errors = [];
page.on('console', (message) => message.type() === 'error' && errors.push(message.text().slice(0, 200)));

// A field or a select is still settling when it appears: the picture waits for it.
const shot = async (name) => {
  if (shots) {
    mkdirSync(shots, { recursive: true });
    await page.waitForTimeout(600);
    await page.screenshot({ path: join(shots, `${name}.png`) });
  }
};

try {
  await page.goto(`${base}/login`, { waitUntil: 'networkidle' });
  await page.locator('input[type=email]').fill('dev-operator@neoprint.local');
  await page.locator('input[type=password]').fill(password);
  await page.locator('button[type=submit]').click();
  await page.waitForURL(/\/operator$/, { timeout: 20_000 });

  // --- From "Mənim kiosklarım" ---------------------------------------------------------------
  const entry = page.locator('[data-action="activate"]');
  await entry.waitFor({ timeout: 20_000 });
  await entry.scrollIntoViewIfNeeded();
  await shot('01-entry');
  await entry.click();
  await page.waitForURL(/\/operator\/activate$/);
  check((await page.locator('h1').innerText()).includes('Kiosk aktivləşdir'), 'the page opens from "Mənim kiosklarım"');
  await shot('02-code');

  // --- A code that is nobody's ---------------------------------------------------------------
  errors.length = 0;
  await page.locator('input[name=code]').fill('ZZZZ-9999');
  await page.locator('button[type=submit]').click();
  const refused = page.locator('.neo-field-error');
  await refused.waitFor({ timeout: 10_000 });
  check((await refused.innerText()).includes('tapılmadı'), 'a wrong code: the page says nothing was found and what to do');
  await shot('03-wrong-code');
  // The refused request is what the browser's console speaks of.
  errors.length = 0;

  // --- The code on the screen ----------------------------------------------------------------
  await page.locator('input[name=code]').fill(machine.pairingCode);
  await page.locator('button[type=submit]').click();
  await page.locator('[data-device]').waitFor({ timeout: 10_000 });
  check((await page.locator('[data-device]').innerText()).includes('e2e-activate-'), 'the right code: the machine is shown by its name');

  await page.locator('mat-select').first().click();
  const option = page.locator('mat-option', { hasText: kiosk.code });
  await option.waitFor();
  await shot('04-which-kiosk');
  await option.click();
  await shot('05-ready');

  await page.locator('button.op-primary.mat-mdc-unelevated-button').click();
  await page.waitForURL(new RegExp(`/operator/kiosks/${kiosk.id}$`), { timeout: 15_000 });
  check(true, "after \"Aktivləşdir\" the kiosk's own page opens");
  await page.locator('h1').waitFor();
  await shot('06-activated');

  const after = await call('GET', `/api/admin/kiosks/${kiosk.id}`);
  check(after.status !== 'NotActivated', `on the server the kiosk has its machine (${after.status})`);
  const bound = (await call('GET', '/api/admin/devices?status=Active')).find((item) => (item.deviceId ?? item.id) === machine.deviceId);
  check(bound?.kioskId === kiosk.id, 'and it is the machine whose code was typed');

  // The code is spent: it finds nothing a second time.
  let spent = false;
  try {
    await call('POST', '/api/admin/devices/lookup', { codeOrQr: machine.pairingCode });
  } catch {
    spent = true;
  }
  check(spent, 'the code works once');
  check(errors.length === 0, `no console errors${errors.length ? `: ${errors.join(' | ')}` : ''}`);
} finally {
  await browser.close();
  takeAway();
}

const left = (await call('GET', '/api/admin/kiosks')).some((entry) => entry.id === kiosk.id);
check(!left, 'nothing of the check is left: its kiosk and both machines are gone');

console.log(failures.length === 0 ? '\nAn operator activated a kiosk from the code on its screen.' : `\n${failures.length} thing(s) wrong.`);
process.exit(failures.length === 0 ? 0 : 1);
