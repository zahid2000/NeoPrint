// ---------------------------------------------------------------------------
// The warehouse form through the panel (T-323): the unit is chosen from a list
// or written as a word - a number is refused where it is typed - a new item is
// made with what is on the shelf as its first purchase, and the table writes
// "20 bağlama", says why an item is low, and never reads a unit as a number.
// Pictures at desktop and phone width; no console error.
//
//   node e2e/warehouse.mjs ../docs/screenshots/step-7b/t323      # from admin/
//
// The items it makes are switched off at the end (an item is never deleted).
// Needs Chrome or Edge.
// ---------------------------------------------------------------------------
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

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

const login = await fetch(`${api}/api/auth/admin/login`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: 'dev-superadmin@neoprint.local', password }),
});
const token = (await login.json()).accessToken;
const call = async (method, path, body) => {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'Idempotency-Key': crypto.randomUUID() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${(await response.text()).slice(0, 200)}`);
  return response.json();
};

const run = Date.now().toString(36).slice(-5).toUpperCase();
const name = `A4 kağız ${run}`;
const browser = await launchBrowser();
const consoleErrors = [];
const watch = (page, label) => {
  page.on('console', (message) => message.type() === 'error' && consoleErrors.push(`${label}: ${message.text()}`));
  page.on('pageerror', (error) => consoleErrors.push(`${label}: ${error.message}`));
};
const shot = async (page, file, fullPage = true) => {
  if (shots) {
    mkdirSync(shots, { recursive: true });
    await page.screenshot({ path: join(shots, `${file}.png`), fullPage });
  }
};
const made = [];

try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, serviceWorkers: 'block' });
  const tab = await context.newPage();
  watch(tab, 'desktop');
  await tab.goto(`${base}/login`, { waitUntil: 'networkidle' });
  await tab.fill('input[type="email"]', 'dev-superadmin@neoprint.local');
  await tab.fill('input[type="password"]', password);
  await tab.click('button[type="submit"]');
  await tab.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });

  await tab.goto(`${base}/inventory`, { waitUntil: 'networkidle' });
  await tab.getByRole('button', { name: 'Yeni məhsul' }).click();
  const dialog = tab.locator('mat-dialog-container');
  await dialog.waitFor();

  check((await dialog.locator('[data-field="unit"]').textContent())?.includes('bağlama'), 'a new paper item is counted in reams');
  check((await dialog.locator('[data-field="initialStock"]').count()) === 1, 'a new item has an initial stock field');

  await dialog.getByLabel('Ad', { exact: true }).fill(name);
  await dialog.getByLabel('SKU').fill(`T323-${run}`);

  // "20" where the unit goes is a quantity: said where it is typed, and nothing is saved.
  await dialog.locator('[data-field="unit"]').click();
  await tab.getByRole('option', { name: 'Digər…' }).click();
  await dialog.locator('[data-field="unitCustom"]').fill('20');
  await tab.waitForTimeout(200);
  check(await dialog.locator('[data-error="unit"]').isVisible(), 'a number as the unit is refused under the field');
  check(await dialog.getByRole('button', { name: 'Yadda saxla' }).isDisabled(), 'and the item cannot be saved');
  await shot(tab, 'warehouse-unit-refused', false);

  await dialog.locator('[data-field="unit"]').click();
  await tab.getByRole('option', { name: 'bağlama' }).click();
  await dialog.getByLabel('Minimal qalıq').fill('25');
  await dialog.locator('[data-field="initialStock"]').fill('20');
  const hints = (await dialog.locator('mat-hint').allTextContents()).join(' | ');
  check(hints.includes('Bir vahiddə (bağlama) neçə vərəq var'), `the hints name the unit: ${hints}`);
  await shot(tab, 'warehouse-new-item', false);
  await dialog.getByRole('button', { name: 'Yadda saxla' }).click();
  await dialog.waitFor({ state: 'detached' });
  await tab.waitForLoadState('networkidle');

  const item = (await call('GET', '/api/admin/inventory/items')).find((row) => row.name === name);
  made.push(item);
  check(item?.stock === 20 && item?.unit === 'ream', `kept: stock ${item?.stock}, unit ${item?.unit}`);

  const row = tab.locator('tr.mat-mdc-row', { hasText: name });
  const stock = (await row.locator('[data-cell="stock"]').innerText()).replace(/\s+/g, ' ');
  check(stock.includes('20 bağlama'), `the stock reads as a quantity and a unit: "${stock}"`);
  check(stock.includes('qalıq 20, minimum 25'), 'the low chip says why');
  check(!/\d\.\d{3}/.test(stock), 'no unit is read as a number');
  await shot(tab, 'warehouse-items');

  // The first purchase, in the movements with its note.
  await tab.getByRole('tab', { name: 'Hərəkətlər' }).click();
  await tab.waitForLoadState('networkidle');
  const first = tab.locator('table:has(th:text-is("Qalıq sonra")) tr.mat-mdc-row', { hasText: name }).first();
  const movement = (await first.innerText()).replace(/\s+/g, ' ');
  check(movement.includes('Alış') && movement.includes('+20') && movement.includes('Başlanğıc qalıq'), `the initial stock is a purchase: "${movement}"`);
  await shot(tab, 'warehouse-movements');

  // Editing changes no count: there is no initial stock to write.
  await tab.getByRole('tab', { name: 'Məhsullar' }).click();
  await row.getByRole('button', { name: 'Redaktə et' }).click();
  await dialog.waitFor();
  check((await dialog.locator('[data-field="initialStock"]').count()) === 0, 'an item being edited has no initial stock');
  await dialog.getByRole('button', { name: 'Ləğv et' }).click();
  await dialog.waitFor({ state: 'detached' });

  // English and Russian: the unit in the reader's language.
  for (const [language, word] of [['en', '20 ream'], ['ru', '20 пачка']]) {
    await tab.evaluate((lang) => localStorage.setItem('neoprint.language', lang), language);
    await tab.reload({ waitUntil: 'networkidle' });
    const text = (await tab.locator('tr.mat-mdc-row', { hasText: name }).locator('[data-cell="stock"]').innerText()).replace(/\s+/g, ' ');
    check(text.includes(word), `${language}: "${text}"`);
  }
  await tab.evaluate(() => localStorage.setItem('neoprint.language', 'az'));

  // Phone width: the table scrolls in its frame, the page does not.
  const phone = await browser.newContext({ viewport: { width: 390, height: 844 }, serviceWorkers: 'block', storageState: await context.storageState() });
  const small = await phone.newPage();
  watch(small, 'phone');
  await small.goto(`${base}/inventory`, { waitUntil: 'networkidle' });
  const overflow = await small.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  check(overflow <= 0, `no sideways scroll at phone width (${overflow}px)`);
  await shot(small, 'warehouse-phone');
  await phone.close();
} finally {
  for (const item of made.filter(Boolean)) {
    await call('PUT', `/api/admin/inventory/items/${item.id}`, { ...item, location: item.location || null, isActive: false });
  }
  await browser.close();
}

check(consoleErrors.length === 0, `no console error${consoleErrors.length ? `: ${consoleErrors.join(' | ')}` : ''}`);
console.log(failures.length ? `\n${failures.length} check(s) failed.` : '\nEvery check passed.');
process.exit(failures.length ? 1 : 0);
