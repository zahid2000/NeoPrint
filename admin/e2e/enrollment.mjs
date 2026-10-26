// ---------------------------------------------------------------------------
// Enrollment tokens through the panel, end to end (T-327): a token made on
// "Cihazlar → Qeydiyyat açarları" is shown once; a new Linux container kiosk
// started with it activates itself on its very first start - nobody reads a
// code off its screen - and the panel shows the token used; another token is
// revoked after a question. Pictures at desktop and phone width; no console
// error. The token is never printed, and is hidden in the pictures.
//
//   node e2e/enrollment.mjs ../docs/screenshots/step-7b/m19a      # from admin/, compose stack up
//
// The kiosk it starts (neoprint-zerotouch) is removed at the end and its
// device revoked; its kiosk stays, NotActivated. Needs Chrome or Edge, Docker.
// ---------------------------------------------------------------------------
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { launchBrowser } from './pages.mjs';

const base = process.env.ADMIN_URL ?? 'http://localhost:8080';
const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const shots = process.argv[2] ? resolve(process.argv[2]) : null;
const container = 'neoprint-zerotouch';
const repo = resolve(process.cwd(), '..');

const failures = [];
const check = (ok, text) => {
  console.log(`${ok ? 'ok    ' : 'WRONG '} ${text}`);
  if (!ok) failures.push(text);
};
const sleep = (ms) => new Promise((done) => setTimeout(done, ms));
const docker = (...args) => { try { return execFileSync('docker', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim(); } catch { return ''; } };

const login = await fetch(`${api}/api/auth/admin/login`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: 'dev-superadmin@neoprint.local', password }),
});
const apiToken = (await login.json()).accessToken;
const call = async (method, path, body) => {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { Authorization: `Bearer ${apiToken}`, 'Content-Type': 'application/json', 'Idempotency-Key': crypto.randomUUID() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status}`);
  return response.status === 204 ? null : response.json();
};

const run = Date.now().toString(36).slice(-4).toUpperCase();
const kioskName = `Sıfır toxunuş ${run}`;
const browser = await launchBrowser();
const consoleErrors = [];
const watch = (page, label) => {
  page.on('console', (message) => message.type() === 'error' && consoleErrors.push(`${label}: ${message.text()}`));
  page.on('pageerror', (error) => consoleErrors.push(`${label}: ${error.message}`));
};
const shot = async (page, file) => {
  if (shots) {
    mkdirSync(shots, { recursive: true });
    await page.screenshot({ path: join(shots, `${file}.png`), fullPage: true });
  }
};
let device = null;

try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, serviceWorkers: 'block' });
  const tab = await context.newPage();
  watch(tab, 'desktop');
  await tab.goto(`${base}/login`, { waitUntil: 'networkidle' });
  await tab.fill('input[type="email"]', 'dev-superadmin@neoprint.local');
  await tab.fill('input[type="password"]', password);
  await tab.click('button[type="submit"]');
  await tab.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });

  await tab.goto(`${base}/devices`, { waitUntil: 'networkidle' });
  await tab.getByRole('button', { name: 'Qeydiyyat açarı yarat' }).click();
  const dialog = tab.locator('mat-dialog-container');
  await dialog.waitFor();

  // A new kiosk, one use, a day.
  await dialog.locator('[data-field="uses"]').fill('1');
  await dialog.locator('[data-field="name"]').fill(kioskName);
  await dialog.locator('[data-field="address"]').fill('Bakı, sınaq (T-327)');
  await dialog.locator('[data-field="pin"]').fill('135790');
  check((await dialog.locator('[data-field="lifetime"]').innerText()).includes('24 saat'), 'the lifetime is chosen from a list, a day by default');
  await shot(tab, 'enrollment-new');
  await dialog.locator('[data-submit]').click();
  await dialog.locator('[data-token]').waitFor();

  const token = (await dialog.locator('[data-token]').innerText()).trim();
  check(/^[0-9a-f]{64}$/.test(token), 'the token is shown once, in full');
  check(await dialog.locator('[data-once-only]').isVisible(), 'with the warning that it is shown only now');
  check((await dialog.locator('[data-token-reach]').innerText()).includes('1 kiosku'), 'and what it can do');
  // The picture shows where the token is, never the token.
  await dialog.locator('[data-token]').evaluate((element) => { element.textContent = '•'.repeat(64); });
  await tab.locator('code.token__command').evaluateAll((elements) => elements.forEach((element) => { element.textContent = element.textContent.replace(/[0-9a-f]{64}/, '•'.repeat(16)); }));
  await shot(tab, 'enrollment-shown-once');
  await dialog.getByRole('button', { name: 'Bitdi' }).click();
  await dialog.waitFor({ state: 'detached' });

  await tab.getByRole('tab', { name: /Qeydiyyat açarları/ }).click();
  await tab.waitForTimeout(500);
  const row = () => tab.locator('tr.mat-mdc-row', { hasText: kioskName });
  check((await row().innerText()).replace(/\s+/g, ' ').includes('0 / 1'), 'the list shows it unused');

  // ---- A new kiosk, started with the token: activated on its first start ----
  console.log('== a new kiosk started with the token');
  docker('rm', '-f', container);
  docker('volume', 'rm', '-f', `${container}-data`);
  execFileSync('bash', [join(repo, 'tools/dev-kiosk/build-linux-kiosk.sh'), '--enrollment-token', token], {
    env: { ...process.env, CONTAINER: container, VOLUME: `${container}-data`, VERSION: '0.9.0-m19a' },
    stdio: ['ignore', 'ignore', 'ignore'],
  });
  const hostname = docker('inspect', '-f', '{{.Config.Hostname}}', container);
  for (let i = 0; i < 30 && !device; i++) {
    await sleep(3_000);
    device = (await call('GET', '/api/admin/devices?status=Active')).find((item) => item.hostname === hostname) ?? null;
  }
  check(!!device, `the kiosk activated itself on its first start${device ? ` as ${device.kioskCode}` : ''}`);
  const pending = (await call('GET', '/api/admin/devices?status=Pending')).filter((item) => item.hostname === hostname);
  check(pending.length === 0, 'it never waited with a code on its screen');
  const kiosk = device ? (await call('GET', '/api/admin/kiosks')).find((item) => item.id === device.kioskId) : null;
  check(kiosk?.name === kioskName, `the kiosk was made from the token's template: "${kiosk?.name}"`);
  check(docker('exec', container, 'sh', '-c', 'test -e /var/lib/neoprint/device/enrollment-token && echo left || echo gone') === 'gone', 'the kiosk deleted the token once it was activated');

  await tab.reload({ waitUntil: 'networkidle' });
  await tab.getByRole('tab', { name: /Qeydiyyat açarları/ }).click();
  await tab.waitForTimeout(500);
  const used = (await row().innerText()).replace(/\s+/g, ' ');
  check(used.includes('1 / 1') && used.includes('İstifadə olunub'), `the panel shows it used: "${used.slice(0, 80)}"`);
  check((await row().locator('[data-revoke]').count()) === 0, 'and offers no revoke for it');

  // ---- Another token, revoked after a question ----
  const spare = await call('POST', '/api/admin/enrollment-tokens', {
    newKiosk: { name: `Ləğv ${run}`, lat: 40.4, lng: 49.8, address: 'Bakı', defaultLanguage: 'az', paperCapacityA4: 500, paperCapacityA3: 0, adminPin: '135790' },
    ttlHours: 1,
  });
  check(!!spare.id, 'another token made');
  await tab.reload({ waitUntil: 'networkidle' });
  await tab.getByRole('tab', { name: /Qeydiyyat açarları/ }).click();
  await tab.waitForTimeout(500);
  const spareRow = tab.locator('tr.mat-mdc-row', { hasText: `Ləğv ${run}` });
  await spareRow.locator('[data-revoke]').click();
  const question = tab.locator('mat-dialog-container');
  await question.waitFor();
  await question.getByText('aktivləşmiş kiosklara təsiri yoxdur').waitFor({ timeout: 5_000 }).catch(() => undefined);
  check((await question.innerText()).includes('aktivləşmiş kiosklara təsiri yoxdur'), 'revoking asks first and says what it does');
  await question.locator('textarea').fill('Sınaq üçün yaradılmışdı');
  await shot(tab, 'enrollment-revoke-question');
  check((await question.getByRole('button', { name: 'Ləğv et', exact: true }).count()) === 1, 'its two buttons say different things');
  await question.getByRole('button', { name: 'Açarı ləğv et' }).click();
  await question.waitFor({ state: 'detached' });
  await tab.waitForTimeout(800);
  check((await spareRow.innerText()).includes('Ləğv edilib'), 'and the token is shown revoked');
  await shot(tab, 'enrollment-list');

  // Phone width.
  const phone = await browser.newContext({ viewport: { width: 390, height: 844 }, serviceWorkers: 'block', storageState: await context.storageState() });
  const small = await phone.newPage();
  watch(small, 'phone');
  await small.goto(`${base}/devices`, { waitUntil: 'networkidle' });
  await small.getByRole('tab', { name: /Qeydiyyat açarları/ }).click();
  await small.waitForTimeout(500);
  const overflow = await small.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  check(overflow <= 0, `no sideways scroll at phone width (${overflow}px)`);
  await shot(small, 'enrollment-phone');
  await phone.close();
}
finally {
  if (device) {
    await call('POST', `/api/admin/devices/${device.id}/revoke`, { reason: 'sıfır toxunuş sınağı bitdi (T-327)' }).catch(() => undefined);
  }
  docker('rm', '-f', container);
  docker('volume', 'rm', '-f', `${container}-data`);
  await browser.close();
}

check(consoleErrors.length === 0, `no console error${consoleErrors.length ? `: ${consoleErrors.join(' | ')}` : ''}`);
console.log(failures.length ? `\n${failures.length} check(s) failed.` : '\nEvery check passed.');
process.exit(failures.length ? 1 : 0);
