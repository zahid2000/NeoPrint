// ---------------------------------------------------------------------------
// Records what the API answers while every page of the panel is opened on the
// development stack, for e2e/render.test.mjs to play back without a backend.
//
//   docker compose up -d                       # the stack, with its seed data
//   node e2e/record.mjs                        # from admin/
//
// Run it again when a page starts asking the API for something new: the test
// says so ("no recorded answer for ...").
//
// The recording goes into the repository, so it is cleaned on the way in:
// tokens, keys, e-mail addresses, host names, network and machine identifiers
// are replaced, and long lists are cut to their first rows. Look at the diff
// of fixtures/api.json before committing it.
// ---------------------------------------------------------------------------
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

import { launchBrowser, menuRoutes } from './pages.mjs';

const base = process.env.ADMIN_URL ?? 'http://localhost:8080';
const email = process.env.NEOPRINT_DEV_ADMIN_EMAIL ?? 'dev-admin@neoprint.local';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const out = join(new URL('.', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'), 'fixtures', 'api.json');

const maxRows = 4;
let people = 0;

function clean(value, key = '') {
  if (Array.isArray(value)) {
    if (/mac/i.test(key)) return ['00:00:00:00:00:00'];
    // Rows are cut short; a list of plain values - permissions, formats, paper sizes - is kept whole.
    const rows = value.some((item) => item && typeof item === 'object') ? value.slice(0, maxRows) : value;
    return rows.map((item) => clean(item, key));
  }

  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([name, inner]) => [name, clean(inner, name)]));
  }

  if (typeof value !== 'string') return value;

  if (/token|secret|password|signature|nonce|hash|publickey|accesskey|secretkey/i.test(key)) return 'recorded-value-removed';
  if (/hostname/i.test(key)) return 'kiosk-host';
  if (/machineid|deviceid$/i.test(key) && !/^[0-9a-f-]{36}$/i.test(value)) return 'machine-id';
  if (/^(ip|localip|ipaddress|remoteip|lastip)$/i.test(key) || /^\d{1,3}(\.\d{1,3}){3}$/.test(value)) return '10.0.0.10';
  if (/serial/i.test(key)) return 'SERIAL-0001';
  if (/useragent/i.test(key)) return 'recorded-browser';
  if (/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(value)) return `person${++people}@example.test`;
  if (/^eyJ[A-Za-z0-9_-]{10,}\./.test(value)) return 'recorded-value-removed';
  if (/^https?:\/\//.test(value) && value.includes('?')) return value.split('?')[0];

  return value;
}

const browser = await launchBrowser();
const context = await browser.newContext({ viewport: { width: 1366, height: 900 }, locale: 'az' });
const page = await context.newPage();
const api = {};
const limited = new Set();

page.on('response', async (response) => {
  const url = new URL(response.url());
  const method = response.request().method();
  const isSession = method === 'POST' && /\/api\/auth\/admin\/(refresh|login)$/.test(url.pathname);

  // The API allows one caller 120 requests a minute. A page that ran into the
  // limit shows nothing, and a recording made of that would be half empty.
  if (url.pathname.startsWith('/api/') && response.status() === 429) limited.add(url.pathname);

  if (!url.pathname.startsWith('/api/') || response.status() !== 200 || (method !== 'GET' && !isSession)) return;
  if (!(response.headers()['content-type'] ?? '').includes('json')) return;

  try {
    const body = clean(await response.json());
    api[`${method} ${url.pathname}${url.search}`] = body;
    // The same page asked with other dates or another page number is answered the same.
    api[`${method} ${url.pathname}`] ??= body;
    if (isSession) api['POST /api/auth/admin/refresh'] = body;
  } catch {
    // Not JSON after all.
  }
});

await page.goto(`${base}/login`, { waitUntil: 'networkidle' });
await page.locator('input[type=email]').fill(email);
await page.locator('input[type=password]').fill(password);
await page.locator('button[type=submit]').click();
await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });

const routes = [];

async function open(route) {
  await page.goto(`${base}/${route}`, { waitUntil: 'networkidle' });
  // Unhurried on purpose: a page a second would run into the API's limit of 120 requests a minute.
  await page.waitForTimeout(3500);

  // A page this administrator may not open sends them back to the dashboard: not a page to test.
  if (new URL(page.url()).pathname.slice(1) !== route) {
    console.log(`skipped /${route}: ${email} is sent to ${new URL(page.url()).pathname}`);
    return;
  }

  routes.push(route);
}

for (const route of menuRoutes) await open(route);

// The first row's own page of each list that has one, and every tab of a kiosk.
for (const list of ['kiosks', 'print-jobs', 'payments', 'customers']) {
  await page.goto(`${base}/${list}`, { waitUntil: 'networkidle' });
  const before = page.url();
  await page.locator('tbody tr').first().click({ timeout: 5000 }).catch(() => {});
  await page.waitForTimeout(1500);

  if (page.url() !== before) {
    const route = new URL(page.url()).pathname.slice(1);
    await open(route);

    if (list === 'kiosks') {
      const tabs = page.locator('.mat-mdc-tab');
      for (let index = 0; index < await tabs.count(); index++) {
        await tabs.nth(index).click();
        await page.waitForTimeout(1200);
      }
    }
  }
}

await browser.close();

if (limited.size > 0) {
  console.error(`The API refused ${limited.size} request(s) as too many (429): wait a minute and record again. Nothing was written.`);
  process.exit(1);
}

mkdirSync(dirname(out), { recursive: true });
writeFileSync(out, JSON.stringify({ recordedAt: new Date().toISOString().slice(0, 10), routes, api }, null, 1) + '\n');
console.log(`${routes.length} pages, ${Object.keys(api).length} answers -> ${out}`);
