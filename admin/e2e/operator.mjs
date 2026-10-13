// ---------------------------------------------------------------------------
// What an operator does most, done through the panel itself on the development
// stack, at a phone's width (T-211): a refill with a ream from the warehouse,
// a task started and finished, and stock taken from the shelf - each checked
// against what the server holds afterwards.
//
//   docker compose up -d                       # the stack and a running kiosk
//   node e2e/operator.mjs                      # from admin/; KSK-9002
//   node e2e/operator.mjs ../docs/screenshots/step-7/m7
//
// It changes the development data: KSK-9002's A4 tray is filled, one "Cleaning"
// task is opened and finished, a toner change is written down, and two units leave the first A4 ream item of
// the warehouse (one used at the kiosk, one taken by the operator). It needs
// such an item with at least two in stock (docs/DEV_ENVIRONMENT.md 7n).
//
// Needs Chrome or Edge on the machine (playwright-core brings no browser).
// ---------------------------------------------------------------------------
import { randomUUID } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { launchBrowser } from './pages.mjs';

const base = process.env.ADMIN_URL ?? 'http://localhost:8080';
const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const shots = process.argv[2] ? resolve(process.argv[2]) : null;
const failures = [];
const check = (ok, text) => { console.log(`${ok ? 'ok    ' : 'WRONG '} ${text}`); if (!ok) failures.push(text); };

async function signIn(email) {
  const response = await fetch(`${api}/api/auth/admin/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }) });
  if (!response.ok) throw new Error(`sign-in ${email}: ${response.status}`);
  return (await response.json()).accessToken;
}

const root = await signIn('dev-superadmin@neoprint.local');
const call = async (method, path, body) => {
  const response = await fetch(`${api}${path}`, { method, headers: { Authorization: `Bearer ${root}`, 'Content-Type': 'application/json', 'Idempotency-Key': randomUUID() }, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${(await response.text()).slice(0, 200)}`);
  return response.status === 204 ? null : response.json();
};

const kiosk = (await call('GET', '/api/admin/kiosks')).find((item) => item.code === 'KSK-9002');
const before = await call('GET', `/api/admin/kiosks/${kiosk.id}/supplies`);
const item = (await call('GET', '/api/admin/inventory/items')).find((entry) => entry.type === 'PaperA4Ream' && entry.isActive);
console.log(`before: A4 ${before.paper.A4.current}/${before.paper.A4.capacity}; warehouse ${item ? `${item.sku} ${item.stock}` : '(no A4 ream)'}`);

// A task for the operator to start and finish.
const task = await call('POST', '/api/admin/service-tasks', { kioskId: kiosk.id, type: 'Cleaning', priority: 'Normal', notes: 'M7 yoxlaması: ekranı silin.' });
console.log(`task ${task.id.slice(0, 8)} ${task.status} → ${task.assignedToName ?? 'nobody'}`);

const browser = await launchBrowser();
const context = await browser.newContext({ viewport: { width: 390, height: 844 }, locale: 'az', isMobile: true, hasTouch: true, serviceWorkers: 'block' });
const page = await context.newPage();
// A dialog is still fading in when its container appears: the picture waits for it to settle.
const shot = async (name) => {
  if (shots) {
    mkdirSync(shots, { recursive: true });
    await page.waitForTimeout(600);
    await page.screenshot({ path: join(shots, `${name}.png`) });
  }
};
const snack = () => page.locator('mat-snack-bar-container').first().innerText({ timeout: 10_000 }).then((text) => text.trim(), () => '(none)');

try {
  await page.goto(`${base}/login`, { waitUntil: 'networkidle' });
  await page.locator('input[type=email]').fill('dev-operator@neoprint.local');
  await page.locator('input[type=password]').fill(password);
  await page.locator('button[type=submit]').click();
  await page.waitForURL(/\/operator$/, { timeout: 20_000 });

  // --- Kağız doldur: "Tam doldur" with one ream from the warehouse -----------------------
  const card = page.locator('article[data-kiosk="KSK-9002"]');
  await card.waitFor({ timeout: 20_000 });
  await card.locator('button.op-primary').click();
  await page.locator('mat-dialog-container').waitFor();
  await shot('09-refill-filled-in');
  await page.locator('mat-dialog-actions button').last().click();
  check((await snack()).includes('Qeyd olundu'), 'refill: the panel says "Qeyd olundu"');
  await page.locator('mat-dialog-container').waitFor({ state: 'detached' });

  const afterRefill = await call('GET', `/api/admin/kiosks/${kiosk.id}/supplies`);
  check(afterRefill.paper.A4.current === afterRefill.paper.A4.capacity, `refill: the tray is full on the server (${afterRefill.paper.A4.current}/${afterRefill.paper.A4.capacity})`);
  const latest = afterRefill.events[0];
  check(latest.type === 'Refill' && latest.resource === 'PaperA4' && latest.after === afterRefill.paper.A4.capacity, `refill: written among the kiosk's movements (${latest.type} ${latest.resource} ${latest.before} -> ${latest.after})`);

  if (item) {
    const stock = (await call('GET', '/api/admin/inventory/items')).find((entry) => entry.id === item.id).stock;
    check(stock === item.stock - 1, `refill: one ream left the warehouse (${item.stock} → ${stock})`);
  }

  await page.waitForTimeout(1200);
  const shown = await card.innerText();
  check(shown.includes(`${afterRefill.paper.A4.capacity} / ${afterRefill.paper.A4.capacity}`), 'refill: the card shows the full tray without a reload');

  // --- Toner dəyişdirildi: from the kiosk's own page ----------------------------------------
  await page.goto(`${base}/operator/kiosks/${kiosk.id}`, { waitUntil: 'networkidle' });
  await page.locator('.op-actions button', { hasText: 'Toner dəyişdirildi' }).click();
  await page.locator('mat-dialog-container').waitFor();
  // The development printer reports no cartridges, so which one is typed in.
  await page.locator('mat-dialog-container input[maxlength="100"]').fill('Black');
  await page.locator('mat-dialog-container textarea').fill('M7 yoxlaması.');
  await shot('12-replaced-dialog');
  await page.locator('mat-dialog-actions button').last().click();
  check((await snack()).includes('Qeyd olundu'), 'replaced: the panel says "Qeyd olundu"');
  await page.locator('mat-dialog-container').waitFor({ state: 'detached' });

  const replaced = (await call('GET', `/api/admin/kiosks/${kiosk.id}/supplies`)).events[0];
  // The cartridge goes with the resource: "Toner:Black".
  check(replaced.type === 'Replaced' && replaced.resource === 'Toner:Black', `replaced: written among the kiosk's movements (${replaced.type} ${replaced.resource})`);

  await page.goto(`${base}/operator`, { waitUntil: 'networkidle' });

  // --- Tapşırıqlar: Başla → Tamamla ---------------------------------------------------------
  await page.locator('nav a[href="/operator/tasks"]').click();
  const row = page.locator(`article[data-task="${task.id}"]`);
  await row.waitFor({ timeout: 20_000 });
  check((await row.locator('button.op-primary').innerText()).includes('Başla'), 'task: its button says "Başla"');
  await row.locator('button.op-primary').click();
  await page.waitForFunction((id) => document.querySelector(`article[data-task="${id}"] button.op-primary`)?.textContent?.includes('Tamamla'), task.id, { timeout: 15_000 });
  check(true, 'task: after "Başla" its button says "Tamamla"');
  await shot('10-task-in-progress');

  await row.locator('button.op-primary').click();
  await page.locator('mat-dialog-container').waitFor();
  await page.locator('mat-dialog-container textarea').fill('Silindi.');
  await page.locator('mat-dialog-actions button').last().click();
  await row.waitFor({ state: 'detached', timeout: 15_000 });
  const done = await call('GET', `/api/admin/service-tasks/${task.id}`);
  check(done.status === 'Done' && done.completion?.note === 'Silindi.', `task: Done on the server with its note (${done.status}, "${done.completion?.note}")`);

  // --- Anbar: Özümə götürürəm ----------------------------------------------------------------
  if (item) {
    const stockBefore = (await call('GET', '/api/admin/inventory/items')).find((entry) => entry.id === item.id).stock;

    await page.locator('nav a[href="/operator/inventory"]').click();
    const shelf = page.locator(`article[data-item="${item.sku}"]`);
    await shelf.waitFor({ timeout: 20_000 });
    await shelf.locator('button.op-primary').click();
    await page.locator('mat-dialog-container').waitFor();
    await shot('11-take-dialog');
    await page.locator('mat-dialog-actions button').last().click();
    await page.locator('mat-dialog-container').waitFor({ state: 'detached' });
    await page.waitForTimeout(1200);

    const movements = await call('GET', `/api/admin/inventory/movements?itemId=${item.id}&pageSize=1`);
    const stockAfter = (await call('GET', '/api/admin/inventory/items')).find((entry) => entry.id === item.id).stock;
    check(stockAfter === stockBefore - 1 && movements.items[0].type === 'IssueToOperator', `take: one unit in the operator's name (${stockBefore} → ${stockAfter}, ${movements.items[0].type})`);
    check((await shelf.innerText()).includes(String(stockAfter)), 'take: the page shows the new stock');
  }
} finally {
  await browser.close();
}

console.log(failures.length === 0 ? '\nThe operator did a refill, a task and a take through the panel.' : `\n${failures.length} thing(s) wrong.`);
process.exit(failures.length === 0 ? 0 : 1);
