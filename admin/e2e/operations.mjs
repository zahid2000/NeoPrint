// ---------------------------------------------------------------------------
// The operations pages on the development stack (T-213), done through the panel
// itself: an incident arrives live and is taken on and closed; a service task
// is created, re-prioritised and called off; a warehouse item is made and its
// stock moved; a kiosk rule is made and deleted; the audit log finds what was
// done. Each page also at a phone's width; no console error, no raw key.
//
//   docker compose up -d
//   node e2e/operations.mjs                        # from admin/
//   node e2e/operations.mjs ../docs/screenshots/step-7/t213
//
// Repeatable (T-266): its kiosk and its warehouse items are its own and are
// removed at the end with everything written about them.
// Needs Chrome or Edge on the machine (playwright-core brings no browser).
// ---------------------------------------------------------------------------
import { randomUUID } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { makeKiosk, purgeItem, purgeKiosk } from './dev-data.mjs';
import { findRawKeys, launchBrowser } from './pages.mjs';

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

// What the check prepares and reads by itself goes as the development Admin, not
// as the SuperAdmin whose panel it drives: the limit is per account (120 a
// minute, T-306), and the two panels of one run already spend most of the
// SuperAdmin's - with its own calls on top, a run started right after another
// was refused (T-299).
const token = await (async () => {
  const response = await fetch(`${api}/api/auth/admin/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: 'dev-admin@neoprint.local', password }),
  });
  return (await response.json()).accessToken;
})();
const call = async (method, path, body) => {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'Idempotency-Key': randomUUID() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${(await response.text()).slice(0, 200)}`);
  return response.status === 204 ? null : response.json();
};

const stamp = Date.now() % 1_000_000;
const kiosk = await makeKiosk(call, 'E2E əməliyyat');
const items = [];
let rule = null;

// The panel on the desktop is driven by an account of the run's own, with the
// SuperAdmin role: the limit is per account (120 a minute, T-306), and two runs
// back to back as one account spend more in a minute than a person would (T-299).
const root = await (async () => {
  const response = await fetch(`${api}/api/auth/admin/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: 'dev-superadmin@neoprint.local', password }),
  });
  return (await response.json()).accessToken;
})();
const asRoot = async (method, path, body) => {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { Authorization: `Bearer ${root}`, 'Content-Type': 'application/json', 'Idempotency-Key': randomUUID() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${(await response.text()).slice(0, 200)}`);
  return response.status === 204 ? null : response.json();
};
const superRole = (await asRoot('GET', '/api/admin/roles')).find((role) => role.isSystem);
const runEmail = `ops-check-${stamp}-${randomUUID().slice(0, 6)}@neoprint.local`;
const firstPassword = `O1-${randomUUID().slice(0, 12)}!a`;
const runPassword = `O2-${randomUUID().slice(0, 12)}!a`;
const runAccount = await asRoot('POST', '/api/admin/admins', {
  email: runEmail, fullName: 'Operations check', password: firstPassword, roleIds: [superRole.id], allKiosks: true, kioskIds: [],
});
{
  const login = await fetch(`${api}/api/auth/admin/login`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: runEmail, password: firstPassword }),
  });
  const first = (await login.json()).accessToken;
  await fetch(`${api}/api/auth/admin/change-password`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${first}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ currentPassword: firstPassword, newPassword: runPassword }),
  });
}

const takeAway = async () => {
  await asRoot('POST', `/api/admin/admins/${runAccount.id}/active`, { isActive: false, reason: 'operations check finished' }).catch(() => undefined);
  items.forEach((id) => purgeItem(api, id));
  if (rule) await call('DELETE', `/api/admin/supply-rules/${rule}`).catch(() => undefined);
  purgeKiosk(api, kiosk.id, [kiosk.deviceId]);
};

const browser = await launchBrowser();
const context = await browser.newContext({ viewport: { width: 1366, height: 900 }, locale: 'az', serviceWorkers: 'block' });
await context.grantPermissions(['notifications'], { origin: base });
const page = await context.newPage();
const errors = [];
page.on('console', (m) => m.type() === 'error' && !/Failed to load resource/.test(m.text()) && errors.push(m.text().slice(0, 200)));
page.on('pageerror', (e) => errors.push(String(e).slice(0, 200)));
// Every answer the panel was refused, with its problem code: what a banner on the page stands for (T-299).
const refused = [];
const sent = [];
page.on('request', (r) => r.url().includes('/api/') && sent.push([Date.now(), new URL(r.url()).pathname]));
page.on('response', async (r) => {
  if (r.url().includes('/api/') && r.status() >= 400) {
    const body = await r.text().catch(() => '');
    refused.push(`${r.status()} ${r.request().method()} ${new URL(r.url()).pathname} ${body.slice(0, 300)}`);
  }
});

const shot = async (name, target = page) => {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  await target.waitForTimeout(500);
  // The repository is public: an address in the picture is hidden first.
  await target.evaluate(() => {
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      node.nodeValue = node.nodeValue.replace(/\b\d{1,3}(\.\d{1,3}){3}\b/g, '•••');
    }
  });
  await target.screenshot({ path: join(shots, `${name}.png`), fullPage: true });
};

async function clean(target, what) {
  await target.waitForLoadState('networkidle').catch(() => undefined);
  await target.locator('main h1').first().waitFor({ timeout: 15_000 }).catch(() => undefined);
  await target.waitForTimeout(700);
  const state = await target.evaluate(() => ({
    heading: !!document.querySelector('main h1'),
    skeletons: document.querySelectorAll('.neo-skeleton').length,
    banners: document.querySelectorAll('app-error-banner').length,
    sideways: document.documentElement.scrollWidth > document.documentElement.clientWidth + 1,
  }));
  const raw = await target.evaluate(findRawKeys);
  check(state.heading && !state.skeletons && !state.banners && !raw.length && !state.sideways,
    `${what}: loaded, no error, no raw key${raw.length ? ` (${raw.join(', ')})` : ''}, no sideways scroll`);
}

const dialog = () => page.locator('mat-dialog-container').last();
const pick = async (field, option) => {
  await field.click();
  await page.locator('mat-option', { hasText: option }).first().click();
};

try {
  await page.goto(`${base}/login`, { waitUntil: 'networkidle' });
  await page.locator('input[type=email]').fill(runEmail);
  await page.locator('input[type=password]').fill(runPassword);
  await page.locator('button[type=submit]').click();
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });

  // --- Incident-lər: one arrives while the page is open ------------------------------------
  await page.goto(`${base}/incidents`, { waitUntil: 'networkidle' });
  await clean(page, 'Incident-lər');
  const low = await call('POST', '/api/admin/inventory/items', {
    sku: `E2E-LOW-${stamp}`, name: `E2E az qalan ${stamp}`, type: 'Other', compatiblePrinterModels: [], unit: 'ədəd', minStock: 5, location: 'E2E', isActive: true,
  });
  items.push(low.id);
  await call('POST', '/api/admin/inventory/movements', { itemId: low.id, type: 'Purchase', quantity: 1 });
  const incidentRow = page.locator('tr.mat-mdc-row', { hasText: `E2E az qalan ${stamp}` }).first();
  await incidentRow.waitFor({ timeout: 30_000 })
    .then(() => check(true, 'incident: a new one (warehouse running low) shows without a reload'))
    .catch(() => check(false, 'incident: the new one did not show'));
  await incidentRow.locator('button', { hasText: 'Baxıram' }).click();
  await incidentRow.filter({ hasText: 'Baxılır' }).waitFor({ timeout: 15_000 })
    .then(() => check(true, 'incident: "Baxıram" — it is taken on'))
    .catch(() => check(false, 'incident: "Baxıram" did not change it'));
  await incidentRow.click();
  await page.locator('tr.ops__detail', { hasText: 'Baxılmağa başlandı' }).first().waitFor({ timeout: 10_000 })
    .then(() => check(true, 'incident: its row opens its history'))
    .catch(async () => {
      check(false, 'incident: its row did not open its history');
      // What the page showed and what it was told (T-299): the cause, not a retry.
      const where = shots ?? resolve('e2e-failure');
      mkdirSync(where, { recursive: true });
      await page.screenshot({ path: join(where, 'history-failure.png'), fullPage: true });
      console.error(`history: open row ${await page.locator('tr.ops__row--open').count()}, detail rows ${await page.locator('tr.ops__detail').count()}; the page at ${join(where, 'history-failure.png')}`);
      refused.forEach((line) => console.error(`refused: ${line}`));
    });
  await shot('10-incidents');
  await incidentRow.locator('button', { hasText: 'Bağla' }).click();
  await dialog().locator('textarea, input').first().fill('E2E: yoxlama üçün bağlandı');
  await dialog().locator('mat-dialog-actions button').last().click();
  await incidentRow.waitFor({ state: 'detached', timeout: 15_000 })
    .then(() => check(true, 'incident: closed with a note, and gone from the open ones'))
    .catch(() => check(false, 'incident: closing did not take it off the list'));

  // --- Servis tapşırıqları: new → higher priority → called off ------------------------------
  await page.goto(`${base}/service-tasks`, { waitUntil: 'networkidle' });
  await clean(page, 'Servis tapşırıqları');
  await page.getByRole('button', { name: 'Yeni tapşırıq' }).click();
  await pick(dialog().locator('mat-select').first(), kiosk.code);
  await dialog().locator('textarea').fill(`E2E ${stamp}: ekranı silin`);
  await dialog().locator('mat-dialog-actions button').last().click();
  const taskRow = page.locator('tr.mat-mdc-row', { hasText: `E2E ${stamp}` }).first();
  await taskRow.waitFor({ timeout: 15_000 });
  check(true, 'task: created from the dialog and listed');
  await shot('11-service-tasks');
  await taskRow.click();
  await dialog().waitFor();
  await pick(dialog().locator('mat-select').nth(1), 'Yüksək');
  await shot('12-task-dialog');
  await dialog().locator('mat-dialog-actions button').last().click();
  await taskRow.filter({ hasText: 'Yüksək' }).waitFor({ timeout: 15_000 })
    .then(() => check(true, 'task: its priority raised from the dialog'))
    .catch(() => check(false, 'task: the priority did not change'));
  await taskRow.click();
  await dialog().locator('mat-dialog-actions button', { hasText: 'Tapşırığı ləğv et' }).click();
  await dialog().locator('textarea, input').first().fill('E2E: lazım deyil');
  await dialog().locator('mat-dialog-actions button').last().click();
  await taskRow.waitFor({ state: 'detached', timeout: 15_000 })
    .then(() => check(true, 'task: called off with a reason, gone from what is to be done'))
    .catch(() => check(false, 'task: calling it off did not take it off the list'));

  // --- Anbar: a new item, bought and corrected ----------------------------------------------
  await page.goto(`${base}/inventory`, { waitUntil: 'networkidle' });
  await clean(page, 'Anbar');
  await page.getByRole('button', { name: 'Yeni məhsul' }).click();
  const inputs = dialog().locator('input');
  await inputs.nth(0).fill(`E2E toner ${stamp}`);
  await inputs.nth(1).fill(`E2E-T-${stamp}`);
  await pick(dialog().locator('mat-select').first(), 'Toner');
  // The unit is a list now and follows the type: a toner by the piece (T-323).
  check((await dialog().locator('[data-field="unit"]').innerText()).includes('ədəd'), 'warehouse: a toner is counted by the piece');
  await dialog().locator('mat-dialog-actions button').last().click();
  const itemRow = page.locator('.mat-mdc-tab-body-active tr.mat-mdc-row', { hasText: `E2E toner ${stamp}` }).first();
  await itemRow.waitFor({ timeout: 15_000 });
  const made = (await call('GET', '/api/admin/inventory/items')).find((entry) => entry.sku === `E2E-T-${stamp}`);
  items.push(made.id);
  check(true, 'warehouse: an item created from the dialog');
  for (const [type, quantity] of [['Alış', '3'], ['Düzəliş', '-1']]) {
    await itemRow.locator('button', { hasText: 'Hərəkət' }).click();
    await pick(dialog().locator('mat-select').first(), type);
    await dialog().locator('input[type=number]').fill(quantity);
    await dialog().locator('mat-dialog-actions button').last().click();
    await dialog().waitFor({ state: 'detached' });
  }
  const stock = (await call('GET', '/api/admin/inventory/items')).find((entry) => entry.id === made.id).stock;
  check(stock === 2, `warehouse: bought 3, corrected by −1 → ${stock}`);
  await page.waitForTimeout(800);
  check((await itemRow.innerText()).includes('2'), 'warehouse: the list shows the new stock');
  await shot('13-inventory');

  // --- Xəbərdarlıq qaydaları: a rule for one kiosk ------------------------------------------
  await page.goto(`${base}/supply-rules`, { waitUntil: 'networkidle' });
  await clean(page, 'Xəbərdarlıq qaydaları');
  await page.getByRole('button', { name: 'Yeni qayda' }).click();
  await dialog().locator('input').first().fill(`E2E qayda ${stamp}`);
  await dialog().locator('mat-select').first().click();
  await page.locator('mat-option', { hasText: kiosk.code }).click();
  await page.keyboard.press('Escape');
  const numbers = dialog().locator('input[type=number]');
  await numbers.nth(0).fill('20');
  await numbers.nth(1).fill('5');
  await dialog().locator('mat-dialog-actions button').last().click();
  const ruleRow = page.locator('tr.mat-mdc-row', { hasText: `E2E qayda ${stamp}` }).first();
  await ruleRow.waitFor({ timeout: 15_000 });
  rule = (await call('GET', '/api/admin/supply-rules')).find((entry) => entry.name === `E2E qayda ${stamp}`)?.id ?? null;
  check((await ruleRow.innerText()).includes(kiosk.code), 'rules: a rule for one kiosk, listed with its code');
  await shot('14-supply-rules');
  await ruleRow.locator('button[aria-label="Sil"]').click();
  await dialog().locator('mat-dialog-actions button').last().click();
  await ruleRow.waitFor({ state: 'detached', timeout: 15_000 })
    .then(() => { rule = null; check(true, 'rules: deleted after asking'); })
    .catch(() => check(false, 'rules: the rule is still there'));

  // --- Audit log: what this check did -------------------------------------------------------
  await page.goto(`${base}/audit`, { waitUntil: 'networkidle' });
  await clean(page, 'Audit jurnalı');
  await page.locator('app-filter-bar input').first().fill('serviceTasks.');
  await page.getByRole('button', { name: 'Axtar' }).click();
  const entry = page.locator('tr.mat-mdc-row').first();
  await entry.waitFor({ timeout: 15_000 });
  check((await entry.innerText()).includes('serviceTasks.'), 'audit: the filter finds the task actions');
  await entry.click();
  await page.locator('tr.ops__detail').first().waitFor({ timeout: 10_000 });
  check(true, 'audit: an entry opens its before / after');
  await shot('15-audit');

  check(errors.length === 0, `desktop: no console error${errors.length ? `: ${errors.join(' | ')}` : ''}`);

  // --- The same pages on a phone ---------------------------------------------------------------
  const phoneContext = await browser.newContext({ viewport: { width: 390, height: 844 }, locale: 'az', isMobile: true, hasTouch: true, serviceWorkers: 'block' });
  const phone = await phoneContext.newPage();
  await phone.goto(`${base}/login`, { waitUntil: 'networkidle' });
  // Another account than the desktop's: one person does not drive two panels
  // at once, and the limit is per account (T-299, T-306). The audit log is not
  // the Admin role's; its phone width is the walk's.
  await phone.locator('input[type=email]').fill('dev-admin@neoprint.local');
  await phone.locator('input[type=password]').fill(password);
  await phone.locator('button[type=submit]').click();
  await phone.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });
  for (const [route, name] of [['incidents', 'Incident-lər'], ['service-tasks', 'Servis tapşırıqları'], ['inventory', 'Anbar'], ['supply-rules', 'Xəbərdarlıq qaydaları']]) {
    await phone.goto(`${base}/${route}`, { waitUntil: 'networkidle' });
    await clean(phone, `${name}, 390 px`);
    await shot(`16-${route}-phone`, phone);
  }
} catch (error) {
  refused.forEach((line) => console.error(`refused: ${line}`));
  // What the page showed when a step gave up, and which step it was (T-299).
  failures.push(String(error.message ?? error).split('\n')[0]);
  console.error(error.message ?? error);
  const where = shots ?? resolve('e2e-failure');
  mkdirSync(where, { recursive: true });
  await page.screenshot({ path: join(where, 'failure.png'), fullPage: true }).catch(() => undefined);
  console.error(`The page at the failure: ${join(where, 'failure.png')}`);
} finally {
  await browser.close();
  await takeAway();
}

{
  const peak = Math.max(0, ...sent.map(([at]) => sent.filter(([other]) => other >= at && other < at + 60_000).length));
  const top = Object.entries(sent.reduce((all, [, path]) => ({ ...all, [path]: (all[path] ?? 0) + 1 }), {})).sort((a, b) => b[1] - a[1]).slice(0, 8);
  console.log(`panel requests: ${sent.length}, most in one minute: ${peak}; ${top.map(([p, n]) => `${n}× ${p}`).join(', ')}`);
}
console.log(failures.length === 0 ? '\nEverything as it should be.' : `\n${failures.length} problem(s).`);
process.exit(failures.length === 0 ? 0 : 1);
