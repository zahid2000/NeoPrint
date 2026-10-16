// ---------------------------------------------------------------------------
// Plan section 9.9 on the development stack, end to end (DoD of step 7A, T-213):
// the paper of a kiosk falls below its rule - a Warning, then a Critical alert
// shows in the admin panel and the operator panel without a reload; a refill
// task opens by itself and goes to an operator; the operator, on a phone,
// starts it and finishes it with a ream from the warehouse; the counter is
// full, the warehouse one ream lighter, the alert closed; the forecast shows.
//
//   docker compose up -d
//   node e2e/supply-flow.mjs                        # from admin/
//   node e2e/supply-flow.mjs ../docs/screenshots/step-7/t213
//
// Repeatable (T-266): the kiosk and the warehouse item are its own, made at the
// start and removed at the end with everything written about them.
// Needs Chrome or Edge on the machine (playwright-core brings no browser).
// ---------------------------------------------------------------------------
import { randomUUID } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { makeKiosk, purgeItem, purgeKiosk } from './dev-data.mjs';
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
const call = async (method, path, body) => {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { Authorization: `Bearer ${root}`, 'Content-Type': 'application/json', 'Idempotency-Key': randomUUID() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${(await response.text()).slice(0, 200)}`);
  return response.status === 204 ? null : response.json();
};

const until = async (what, test, seconds = 20) => {
  const end = Date.now() + seconds * 1000;
  for (;;) {
    const value = await test();
    if (value) return value;
    if (Date.now() > end) throw new Error(`${what}: not within ${seconds} s`);
    await new Promise((r) => setTimeout(r, 500));
  }
};

const operatorEmail = 'dev-operator@neoprint.local';
const kiosk = await makeKiosk(call, 'E2E 9.9');
let item = null;

const takeAway = () => {
  if (item) purgeItem(api, item.id);
  purgeKiosk(api, kiosk.id, [kiosk.deviceId]);
};

try {
  await call('POST', `/api/admin/kiosks/${kiosk.id}/paper/refill`, { paper: 'A4', mode: 'Exact', sheets: 480 });
  item = await call('POST', '/api/admin/inventory/items', {
    sku: `E2E-99-${Date.now() % 1_000_000}`,
    name: 'E2E 9.9 A4 kağız',
    type: 'PaperA4Ream',
    compatiblePrinterModels: [],
    unit: 'bağlama',
    sheetsPerUnit: 500,
    minStock: 0,
    location: 'E2E',
    isActive: true,
  });
  item = await call('POST', '/api/admin/inventory/movements', { itemId: item.id, type: 'Purchase', quantity: 2 });
} catch (error) {
  takeAway();
  throw error;
}

console.log(`${kiosk.code}: A4 480/500, shelf ${item.sku} ${item.stock}`);

const browser = await launchBrowser();
const desk = await (await browser.newContext({ viewport: { width: 1366, height: 900 }, locale: 'az', serviceWorkers: 'block' })).newPage();
const phoneContext = await browser.newContext({ viewport: { width: 390, height: 844 }, locale: 'az', isMobile: true, hasTouch: true, serviceWorkers: 'block' });
const phone = await phoneContext.newPage();
const errors = [];
for (const page of [desk, phone]) {
  page.on('console', (m) => m.type() === 'error' && !/Failed to load resource/.test(m.text()) && errors.push(m.text().slice(0, 200)));
}

const shot = async (page, name) => {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  await page.waitForTimeout(500);
  await page.screenshot({ path: join(shots, `${name}.png`) });
};

async function login(page, email) {
  await page.goto(`${base}/login`, { waitUntil: 'networkidle' });
  await page.locator('input[type=email]').fill(email);
  await page.locator('input[type=password]').fill(password);
  await page.locator('button[type=submit]').click();
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });
}

try {
  await login(desk, 'dev-superadmin@neoprint.local');
  await desk.goto(`${base}/supply-rules`, { waitUntil: 'networkidle' });
  await desk.locator('main h1').waitFor();
  await login(phone, operatorEmail);
  const card = phone.locator(`article[data-kiosk="${kiosk.code}"]`);
  await card.waitFor({ timeout: 20_000 });

  // --- 1. Below 200 sheets: a Warning, in both panels, without a reload -------
  await call('POST', `/api/admin/kiosks/${kiosk.id}/paper/refill`, { paper: 'A4', mode: 'Exact', sheets: 150 });
  const alertRow = desk.locator('tr.mat-mdc-row', { hasText: kiosk.code });
  await alertRow.filter({ hasText: 'Xəbərdarlıq' }).waitFor({ timeout: 20_000 })
    .then(() => check(true, 'Warning: the admin panel\'s alerts list shows it without a reload'))
    .catch(() => check(false, 'Warning: the admin panel did not show it'));
  await until('operator card', async () => (await card.innerText()).includes('150'), 20)
    .then(() => check(true, 'Warning: the operator\'s card shows 150 sheets without a reload'))
    .catch(() => check(false, 'Warning: the operator\'s card did not follow'));
  await shot(desk, '01-warning-admin');
  await shot(phone, '02-warning-operator');

  // --- 2. Below 50: Critical, and the task the rule opens ----------------------
  await call('POST', `/api/admin/kiosks/${kiosk.id}/paper/refill`, { paper: 'A4', mode: 'Exact', sheets: 40 });
  await alertRow.filter({ hasText: 'Kritik' }).waitFor({ timeout: 20_000 })
    .then(() => check(true, 'Critical: the admin panel\'s alert turns "Kritik" without a reload'))
    .catch(() => check(false, 'Critical: the admin panel did not follow'));

  const task = await until('the task', async () => {
    const tasks = (await call('GET', `/api/admin/service-tasks?kioskId=${kiosk.id}&status=Open&status=Assigned&status=InProgress`)).items;
    return tasks.find((entry) => entry.type === 'RefillPaper' && entry.priority === 'Critical');
  });
  check(task.source === 'AlertRule', `task: opened by the rule (${task.type}, ${task.priority}, ${task.source})`);
  check(!!task.assignedTo, `task: given to an operator at once (${task.assignedToName ?? 'nobody'})`);

  if (task.assignedToName && !task.assignedToName.toLowerCase().includes('operator')) {
    console.log(`       (the system chose ${task.assignedToName}; the check gives it to the development operator to carry on)`);
  }

  const devOperator = (await call('GET', `/api/admin/service-tasks/assignees?kioskId=${kiosk.id}`)).find((person) => person.fullName.toLowerCase().includes('operator') && person.operator);
  if (devOperator && task.assignedTo !== devOperator.id) {
    await call('POST', `/api/admin/service-tasks/${task.id}/assign`, { assignTo: devOperator.id });
  }

  await desk.goto(`${base}/service-tasks`, { waitUntil: 'networkidle' });
  const taskRow = desk.locator('tr.mat-mdc-row', { hasText: kiosk.code });
  await taskRow.first().waitFor({ timeout: 15_000 });
  check((await taskRow.first().innerText()).includes('Kağız doldurmaq'), 'admin: the task is on "Servis tapşırıqları" with its kiosk');
  await shot(desk, '03-task-admin');

  await desk.goto(`${base}/kiosks/${kiosk.id}`, { waitUntil: 'networkidle' });
  await desk.getByRole('tab', { name: 'Sərf', exact: true }).click();
  await desk.locator('text=Proqnoz').first().waitFor({ timeout: 15_000 });
  const forecast = await desk.locator('app-kiosk-supplies').innerText();
  check(forecast.includes('Proqnoz') && forecast.includes('A4 kağızı'), 'forecast: the kiosk\'s supplies show the forecast for A4');
  await shot(desk, '04-forecast');

  // --- 3. The operator finishes it from the phone, with a ream -----------------
  await phone.goto(`${base}/operator/tasks`, { waitUntil: 'networkidle' });
  const row = phone.locator(`article[data-task="${task.id}"]`);
  await row.waitFor({ timeout: 20_000 });
  await shot(phone, '05-task-operator');
  await row.locator('button.op-primary').click();
  await phone.waitForFunction((id) => document.querySelector(`article[data-task="${id}"] button.op-primary`)?.textContent?.includes('Tamamla'), task.id, { timeout: 15_000 });
  await row.locator('button.op-primary').click();
  await phone.locator('mat-dialog-container').waitFor();
  await phone.locator('mat-dialog-container mat-select').click();
  await phone.locator('mat-option', { hasText: item.name }).click();
  await shot(phone, '06-complete-dialog');
  await phone.locator('mat-dialog-actions button').last().click();
  await row.waitFor({ state: 'detached', timeout: 15_000 });

  // --- 4. What the server holds afterwards --------------------------------------
  const done = await call('GET', `/api/admin/service-tasks/${task.id}`);
  check(done.status === 'Done', `task: Done (${done.status})`);
  const supplies = await call('GET', `/api/admin/kiosks/${kiosk.id}/supplies`);
  check(supplies.paper.A4.current === supplies.paper.A4.capacity, `counter: full (${supplies.paper.A4.current}/${supplies.paper.A4.capacity})`);
  const stock = (await call('GET', '/api/admin/inventory/items')).find((entry) => entry.id === item.id).stock;
  check(stock === item.stock - 1, `warehouse: one ream fewer (${item.stock} → ${stock})`);
  const open = await until('the alert to close', async () => {
    const alerts = (await call('GET', `/api/admin/supply-alerts?active=true&kioskId=${kiosk.id}`)).items;
    return alerts.length === 0 ? 'closed' : null;
  });
  check(open === 'closed', 'alert: closed by itself');

  await desk.goto(`${base}/supply-rules`, { waitUntil: 'networkidle' });
  await desk.waitForTimeout(800);
  check(!(await desk.locator('tr.mat-mdc-row', { hasText: kiosk.code }).count()), 'admin: the kiosk is no longer among the alerts');
  await shot(desk, '07-alert-closed');
  await desk.goto(`${base}/inventory`, { waitUntil: 'networkidle' });
  await desk.getByRole('tab', { name: 'Hərəkətlər' }).click();
  await desk.locator('.mat-mdc-tab-body-active tr.mat-mdc-row', { hasText: item.name }).first().waitFor({ timeout: 15_000 });
  check((await desk.locator('.mat-mdc-tab-body-active tr.mat-mdc-row', { hasText: item.name }).first().innerText()).includes('Kioskda işləndi'), 'warehouse page: the ream used at the kiosk is among the movements');
  await shot(desk, '08-inventory-movements');

  check(errors.length === 0, `no console error${errors.length ? `: ${errors.join(' | ')}` : ''}`);
} finally {
  await browser.close();
  takeAway();
}

console.log(failures.length === 0 ? '\nPlan 9.9 went through end to end.' : `\n${failures.length} thing(s) wrong.`);
process.exit(failures.length === 0 ? 0 : 1);
