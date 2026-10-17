// ---------------------------------------------------------------------------
// The pilot report on the development stack (step 7A, M10 - T-214; plan
// section 2.17): a kiosk of the check's own goes through the day's scenarios
// as the kiosk application does them - visits, files, a paid print, a payment
// the bank declines, a print the printer stops (refunded by itself), an order
// cancelled before paying, a print refunded by an administrator - and the
// figures the report shows are compared with the ones worked out here by hand.
// Then: the same in a phone's width for an account that sees this kiosk only,
// a print that arrives while the page is open, the CSV, and the rollup run
// twice over the day without counting anything twice.
//
//   docker compose up -d
//   node e2e/pilot-report.mjs                        # from admin/
//   node e2e/pilot-report.mjs ../docs/screenshots/step-7/t214
//
// Repeatable (T-266): the kiosk, its orders and their money are removed at
// the end with everything written about them; the account is switched off.
// Needs Chrome or Edge on the machine (playwright-core brings no browser).
// ---------------------------------------------------------------------------
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { kioskSession, makeKiosk, purgeKiosk } from './dev-data.mjs';
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
const same = (actual, expected, what) => check(JSON.stringify(actual) === JSON.stringify(expected), `${what}: ${JSON.stringify(actual)}${JSON.stringify(actual) === JSON.stringify(expected) ? '' : ` (expected ${JSON.stringify(expected)})`}`);

async function signIn(email, secret = password) {
  for (let tries = 0; ; tries++) {
    const response = await fetch(`${api}/api/auth/admin/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password: secret }),
    });
    // The API takes ten sign-ins a minute; a run right after another waits it out.
    if (response.status === 429 && tries < 2) {
      console.log('       (too many sign-ins in a minute: waiting)');
      await new Promise((r) => setTimeout(r, 61_000));
      continue;
    }
    if (!response.ok) throw new Error(`sign-in: ${response.status}`);
    return (await response.json()).accessToken;
  }
}

const caller = (token) => async (method, path, body) => {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'Idempotency-Key': randomUUID() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${(await response.text()).slice(0, 200)}`);
  return response.status === 204 ? null : response.json();
};

const until = async (what, test, seconds = 30) => {
  const end = Date.now() + seconds * 1000;
  for (;;) {
    const value = await test();
    if (value) return value;
    if (Date.now() > end) throw new Error(`${what}: not within ${seconds} s`);
    await new Promise((r) => setTimeout(r, 500));
  }
};

/** The mock bank, as a customer's phone would answer it (docs/DEV_ENVIRONMENT.md section 7c). */
const bank = async (paymentId, outcome) => {
  const response = await fetch(`${api}/api/dev/payments/${paymentId}/simulate-${outcome}`, { method: 'POST' });
  if (!response.ok) throw new Error(`mock bank ${outcome}: ${response.status}`);
};

const root = await signIn('dev-superadmin@neoprint.local');
const call = caller(root);
const kiosk = await makeKiosk(call, 'E2E pilot');
let account = null;

// Whatever stops the run, the kiosk and everything written about it go (purgeKiosk is synchronous).
let purged = false;
const takeAway = () => {
  if (!purged) {
    purged = true;
    purgeKiosk(api, kiosk.id, [kiosk.deviceId]);
  }
};
process.on('exit', takeAway);

const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Baku' }).format(new Date());

try {
  await call('POST', `/api/admin/kiosks/${kiosk.id}/paper/refill`, { paper: 'A4', mode: 'Exact', sheets: 500 });
} catch (error) {
  takeAway();
  throw error;
}

const asKiosk = await kioskSession(api, kiosk.deviceId, kiosk.privateKey);
const heartbeat = () => asKiosk('POST', '/api/kiosks/heartbeat', { appVersion: '0.7.0', agentVersion: '0.7.0', printerOnline: true });

// ---------------------------------------------------------------------------
// What the kiosk does, the way the application does it
// ---------------------------------------------------------------------------

const options = { copies: 1, pageRange: null, color: false, duplex: false, paper: 'A4', orientation: 'Auto', pagesPerSheet: 1, scaling: 'Fit' };

/** A visit's events; the kiosk sends them in a batch, as its queue does. */
const visit = () => {
  const session = randomUUID().replaceAll('-', '');
  const events = [];
  let at = Date.now() - 60_000;
  const track = (event, props = {}) => {
    at += 2_000;
    events.push({ event, sessionId: session, at: new Date(at).toISOString(), props });
  };
  return { events, track };
};

const send = (events) => asKiosk('POST', '/api/kiosks/me/analytics', { events });

async function order(file, pages) {
  await heartbeat();
  return asKiosk('POST', '/api/print-jobs', { localFileName: file, pageCount: pages, options });
}

async function pay(job, outcome) {
  const payment = await asKiosk('POST', '/api/payments/intents', { jobId: job.id, method: 'BankQr' });
  await bank(payment.id, outcome);
  await until(`payment ${outcome}`, async () => {
    const now = await asKiosk('GET', `/api/payments/${payment.id}`);
    return outcome === 'success' ? now.status === 'Succeeded' : now.status === 'Failed';
  });
  return payment;
}

async function print(job, sheets, seconds, error = null) {
  await until('the job to be queued', async () => (await asKiosk('GET', `/api/print-jobs/${job.id}`)).status === 'Queued');
  const started = new Date(Date.now() - seconds * 1000).toISOString();
  const finished = new Date().toISOString();
  const attempt = (end) => ({ number: 1, printer: 'Test printer', spoolerJobId: null, startedAt: started, finishedAt: end, counterBefore: null, counterAfter: null, counterUnit: null, recovered: false });

  await asKiosk('POST', `/api/print-jobs/${job.id}/status`, { status: 'Printing', printedSides: 0, printedSheets: 0, errorCode: null, attempt: attempt(null) });
  await asKiosk('POST', `/api/print-jobs/${job.id}/status`, error
    ? { status: 'Interrupted', printedSides: 0, printedSheets: 0, errorCode: error, attempt: attempt(finished) }
    : { status: 'Completed', printedSides: sheets, printedSheets: sheets, errorCode: null, attempt: attempt(finished) });
}

/** A visit that ends in an order: the file, the options screen, the order, the payment screen. */
function flashVisit(format, job) {
  const v = visit();
  v.track('screen_view', { screen: 'Browse' });
  v.track('screen_view', { screen: 'Preview' });
  v.track('file_selected', { format, pages: '2', sizeMb: '0.2' });
  v.track('screen_view', { screen: 'Options' });
  v.track('price_shown', { total: '0.40', currency: 'AZN' });
  v.track('options_set', { copies: '1', color: 'false', duplex: 'false', paper: 'A4', pagesPerSheet: '1', range: 'all' });
  v.track('order_placed', { sheets: '2', jobCode: job.jobCode });
  v.track('screen_view', { screen: 'PaymentMethod' });
  return v;
}

// ---------------------------------------------------------------------------
// The day, scenario by scenario
// ---------------------------------------------------------------------------

const amounts = {};
const shotsDone = [];

try {
  // S1 - paid by the bank, printed in 45 s.
  const s1 = await order('e2e-hesabat.pdf', 2);
  const v1 = flashVisit('pdf', s1);
  v1.track('payment_started', { method: 'BankQr' });
  amounts.s1 = (await pay(s1, 'success')).amount;
  v1.track('payment_succeeded', { method: 'BankQr', seconds: '25' });
  await print(s1, 2, 45);
  await send(v1.events);

  // S2 - the bank declines; the customer cancels the order and leaves.
  const s2 = await order('e2e-muqavile.docx', 2);
  const v2 = flashVisit('docx', s2);
  v2.track('payment_started', { method: 'BankQr' });
  await pay(s2, 'fail');
  v2.track('payment_failed', { method: 'BankQr', seconds: '18' });
  await asKiosk('POST', `/api/print-jobs/${s2.id}/cancel`);
  v2.track('flow_abandoned', { lastScreen: 'PaymentExpired', seconds: '70' });
  await send(v2.events);

  // S4 - the order is cancelled on the payment method screen, before paying.
  const s4 = await order('e2e-sekil.jpg', 1);
  const v4 = flashVisit('jpg', s4);
  await asKiosk('POST', `/api/print-jobs/${s4.id}/cancel`);
  v4.track('flow_abandoned', { lastScreen: 'PaymentMethod', seconds: '40' });
  await send(v4.events);

  // S6 - a file that would not open; S7 - a visit that only looked.
  const v6 = visit();
  v6.track('screen_view', { screen: 'Browse' });
  v6.track('conversion_failed', { format: 'xlsx', error: 'pdf.damaged' });
  v6.track('flow_abandoned', { lastScreen: 'Browse', seconds: '30' });
  const v7 = visit();
  v7.track('screen_view', { screen: 'Browse' });
  v7.track('flow_abandoned', { lastScreen: 'Browse', seconds: '15' });
  await send([...v6.events, ...v7.events]);

  // S3 - paid, and the printer jams at the first sheet: the backend cancels
  // the order and gives the money back by itself (plan section 2.5, scenario A).
  const s3 = await order('e2e-arayis.pdf', 2);
  const v3 = flashVisit('pdf', s3);
  v3.track('payment_started', { method: 'BankQr' });
  amounts.s3 = (await pay(s3, 'success')).amount;
  v3.track('payment_succeeded', { method: 'BankQr', seconds: '30' });
  await print(s3, 2, 20, 'printer.paperJam');
  await send(v3.events);
  await until('the automatic refund', async () => {
    const refunds = await asKiosk('GET', `/api/print-jobs/${s3.id}/refunds`);
    return refunds.some((r) => r.status === 'Succeeded');
  });
  // The jam stopped the kiosk; the printer is ready again.
  await heartbeat();
} catch (error) {
  takeAway();
  throw error;
}

// ---------------------------------------------------------------------------
// The figures, by hand
// ---------------------------------------------------------------------------

const money = (value) => Math.round(value * 100) / 100;
const expected = (withS5) => ({
  // S1, S2, S4, S6, S7, S3 - and S5.
  visits: withS5 ? 7 : 6,
  orders: withS5 ? 5 : 4,
  // Chosen: the four orders and S6 (it did not open); paid and printing: S1, S3; printed: S1 (S3 jammed).
  funnel: withS5 ? [6, 5, 5, 5, 3, 3, 2] : [5, 4, 4, 4, 2, 2, 1],
  printsSucceeded: withS5 ? 2 : 1,
  printsFailed: 1,
  // S2 and S4 by the customer, S3 by the backend before its refund.
  cancelled: 3,
  refunds: withS5 ? 2 : 1,
  sales: money(amounts.s1 + amounts.s3 + (withS5 ? amounts.s5 : 0)),
  net: money(amounts.s1),
  averagePrintSeconds: withS5 ? 60 : 45,
  paperSheets: withS5 ? 4 : 2,
  paymentsStarted: withS5 ? 4 : 3,
  paymentsSucceeded: withS5 ? 3 : 2,
});

const report = (token, kioskId) => caller(token)('GET', `/api/admin/analytics/pilot?from=${today}&to=${today}${kioskId ? `&kioskId=${kioskId}` : ''}`);

function compare(r, e, who) {
  const t = r.totals;
  same(t.visits, e.visits, `${who}: visits`);
  same(t.orders, e.orders, `${who}: orders`);
  same(r.funnel.map((s) => s.count), e.funnel, `${who}: funnel (file, preview, options, payment screen, paid, printing, printed)`);
  same(t.printsSucceeded, e.printsSucceeded, `${who}: prints that succeeded`);
  same(t.printsFailed, e.printsFailed, `${who}: prints that failed`);
  same(t.cancelled, e.cancelled, `${who}: cancelled orders`);
  same(t.refunds, e.refunds, `${who}: refunds`);
  same(money(t.sales), e.sales, `${who}: sales`);
  same(money(t.net), e.net, `${who}: net`);
  same(t.averagePrintSeconds, e.averagePrintSeconds, `${who}: average print time`);
  same(t.paperSheets, e.paperSheets, `${who}: paper`);
  same(t.paymentsStarted, e.paymentsStarted, `${who}: payments started`);
  same(t.paymentsSucceeded, e.paymentsSucceeded, `${who}: payments that went through`);
}

const first = await report(root, kiosk.id);
const e1 = expected(false);
compare(first, e1, 'API, before S5');
same(first.formats.map((f) => [f.format, f.selected, f.failed]), [['pdf', 2, 0], ['docx', 1, 0], ['jpg', 1, 0], ['xlsx', 1, 1]], 'API: formats');
same(first.paymentFailures.map((x) => [x.key, x.count]), [['payments.declined', 1]], 'API: payment failures by reason');
check(!!first.paymentFailures[0]?.text, `API: the reason reads as text ("${first.paymentFailures[0]?.text}")`);
same(first.printErrors.map((x) => [x.key, x.count]), [['printer.paperJam', 1]], 'API: print errors');
same(first.conversionErrors.map((x) => [x.key, x.count]), [['pdf.damaged', 1]], 'API: conversion errors');
same(first.abandoned.map((x) => [x.key, x.count]), [['Browse', 2], ['PaymentExpired', 1], ['PaymentMethod', 1]], 'API: where visits ended');
same(first.refundReasons.map((x) => [x.key, x.count]), [['SystemError', 1]], 'API: refund reasons');
same(first.methods.map((m) => [m.method, m.started, m.succeeded]), [['BankQr', 3, 2]], 'API: by method');

const incidents = (await call('GET', `/api/admin/incidents?kioskId=${kiosk.id}&pageSize=100`)).items.length;
same(first.totals.incidents, incidents, 'API: incidents, as many as the incidents list has for the kiosk');

// ---------------------------------------------------------------------------
// The page: a desk and a phone
// ---------------------------------------------------------------------------

const browser = await launchBrowser();
const errors = [];
const watch = (page) => page.on('console', (m) => m.type() === 'error' && !/Failed to load resource/.test(m.text()) && errors.push(m.text().slice(0, 200)));

const shot = async (page, name) => {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  // No address of this machine in a picture that goes into a public repository.
  await page.evaluate(() => {
    for (const node of document.querySelectorAll('body *')) {
      if (node.children.length === 0 && /\b\d{1,3}(\.\d{1,3}){3}\b/.test(node.textContent ?? '')) node.textContent = '10.0.0.10';
    }
  });
  await page.waitForTimeout(400);
  await page.screenshot({ path: join(shots, `${name}.png`), fullPage: true });
  shotsDone.push(name);
};

async function login(page, email, secret = password) {
  await page.goto(`${base}/login`, { waitUntil: 'networkidle' });
  await page.locator('input[type=email]').fill(email);
  await page.locator('input[type=password]').fill(secret);
  await page.locator('button[type=submit]').click();
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });
}

const stat = async (page, name) => (await page.locator(`[data-stat="${name}"] .pilot__stat-value`).innerText()).replace(/\s+/g, ' ').trim();
const steps = async (page) => Promise.all((await page.locator('.pilot__step-count').all()).map(async (node) => Number((await node.innerText()).replace(/\D/g, ''))));

try {
  const deskContext = await browser.newContext({ viewport: { width: 1366, height: 900 }, locale: 'az', serviceWorkers: 'block', acceptDownloads: true });
  const desk = await deskContext.newPage();
  watch(desk);
  await login(desk, 'dev-superadmin@neoprint.local');
  await desk.goto(`${base}/pilot-report`, { waitUntil: 'networkidle' });
  await desk.locator('main h1').waitFor();
  await desk.locator('mat-select').last().click();
  await desk.locator('mat-option', { hasText: kiosk.code }).click();
  await desk.locator('[data-stat="printed"]').waitFor({ timeout: 15_000 });
  await desk.waitForTimeout(800);

  same(await stat(desk, 'printed'), String(e1.printsSucceeded), 'page: prints that succeeded');
  same(await stat(desk, 'failed'), `error ${e1.printsFailed}`, 'page: failed prints, with its icon');
  same(await stat(desk, 'cancelled'), String(e1.cancelled), 'page: cancelled orders');
  same(await stat(desk, 'refunds'), String(e1.refunds), 'page: refunds');
  same(await steps(desk), e1.funnel, 'page: the funnel');
  check((await desk.locator('[data-step="paid"] .pilot__loss--worst').count()) === 1, 'page: the biggest loss (payment screen → paid) is marked');
  check((await desk.locator('[data-format="xlsx"]').innerText()).includes('XLSX'), 'page: formats by their extension');
  check((await desk.locator('[data-card="payments"]').innerText()).includes(first.paymentFailures[0].text), 'page: the bank\'s reason in words');
  check((await desk.locator('[data-card="abandoned"]').innerText()).includes('Ödəniş üsulu'), 'page: the screens by their names');
  check((await desk.locator(`tr[data-day="${today}"]`).innerText()).includes('Canlı'), 'page: today is live');
  await shot(desk, '01-pilot-desk');

  // S5 while the page is open: paid, printed in 75 s, refunded by an administrator.
  const s5 = await order('e2e-cedvel.pdf', 2);
  const v5 = flashVisit('pdf', s5);
  v5.track('payment_started', { method: 'BankQr' });
  const p5 = await pay(s5, 'success');
  amounts.s5 = p5.amount;
  v5.track('payment_succeeded', { method: 'BankQr', seconds: '20' });
  await print(s5, 2, 75);
  await send(v5.events);
  await call('POST', `/api/admin/payments/${p5.id}/refund`, { amount: null, destination: 'OriginalMethod', reason: 'E2E: pilot hesabatının yoxlaması', cancelJob: false });
  await until('the administrator\'s refund', async () => {
    const refunds = await asKiosk('GET', `/api/print-jobs/${s5.id}/refunds`);
    return refunds.some((r) => r.status === 'Succeeded');
  });

  const e2 = expected(true);
  await until('the page to follow without a reload', async () => (await stat(desk, 'printed')) === String(e2.printsSucceeded) && (await stat(desk, 'refunds')) === String(e2.refunds), 30)
    .then(() => check(true, 'page: S5 and its refund arrive without a reload'))
    .catch(() => check(false, 'page: did not follow S5 without a reload'));
  same(await steps(desk), e2.funnel, 'page: the funnel after S5');
  compare(await report(root, kiosk.id), e2, 'API, after S5');

  // The CSV: in the reader's language, and nothing of a file's name.
  const [download] = await Promise.all([desk.waitForEvent('download'), desk.locator('.pilot__csv').click()]);
  const csv = readFileSync(await download.path(), 'utf8');
  // The page opens on the last 14 days, so the name begins with that day.
  const csvName = download.suggestedFilename();
  check(csvName.startsWith('neoprint-pilot-') && csvName.endsWith(`-${today}-${kiosk.code}.csv`), `CSV: named ${csvName}`);
  check(csv.charCodeAt(0) === 0xfeff && csv.slice(1).startsWith('Gün;Ziyarət;Sifariş;Uğurlu çap'), 'CSV: headed in Azerbaijani, with the byte-order mark');
  check(csv.includes(`${today};${e2.visits};${e2.orders};${e2.printsSucceeded};${e2.printsFailed};`), 'CSV: today\'s row has the same figures');
  check(!/e2e-|hesabat\.pdf|muqavile|cedvel|arayis|@/.test(csv), 'CSV: no file name, no e-mail');
  await shot(desk, '02-pilot-desk-after-s5');
  await desk.locator('.pilot__grid').scrollIntoViewIfNeeded();
  await shot(desk, '03-pilot-desk-cards');
  await desk.locator('table.neo-table').scrollIntoViewIfNeeded();
  await shot(desk, '04-pilot-desk-days');

  // A phone, and an account that looks after this kiosk only.
  const stamp = new Date().toISOString().replace(/\D/g, '').slice(0, 14);
  const role = await call('POST', '/api/admin/roles', {
    name: `Pilot check ${stamp}`,
    description: 'Made by admin/e2e/pilot-report.mjs; safe to delete.',
    permissions: ['analytics.view'],
  });
  const firstSecret = `P1-${randomBytes(9).toString('base64url')}!a`;
  const secret = `P2-${randomBytes(9).toString('base64url')}!a`;
  const email = `pilot-check-${stamp}@neoprint.local`;
  account = await call('POST', '/api/admin/admins', { email, fullName: 'Pilot check', password: firstSecret, roleIds: [role.id], allKiosks: false, kioskIds: [kiosk.id] });
  await caller(await signIn(email, firstSecret))('POST', '/api/auth/admin/change-password', { currentPassword: firstSecret, newPassword: secret });

  const scopedReport = await report(await signIn(email, secret));
  compare(scopedReport, e2, 'API, the scoped account with "all kiosks"');
  check(scopedReport.appFormats === undefined, 'API: the scoped account is not shown the app\'s uploads');

  const phone = await (await browser.newContext({ viewport: { width: 390, height: 844 }, locale: 'az', isMobile: true, hasTouch: true, serviceWorkers: 'block' })).newPage();
  watch(phone);
  await login(phone, email, secret);
  await phone.goto(`${base}/pilot-report`, { waitUntil: 'networkidle' });
  await phone.locator('[data-stat="printed"]').waitFor({ timeout: 15_000 });
  same(await stat(phone, 'printed'), String(e2.printsSucceeded), 'phone, scoped: prints that succeeded are this kiosk\'s only');
  check(!(await phone.locator('text=Tətbiqdən yüklənən').count()), 'phone, scoped: no app uploads');
  check(await phone.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), 'phone: no sideways scroll at 390 px');
  await shot(phone, '05-pilot-phone-scoped');
  await phone.locator('.pilot__funnel').scrollIntoViewIfNeeded();
  await shot(phone, '06-pilot-phone-funnel');

  // The rollup over today, twice: the same figures, one document.
  await call('POST', '/api/admin/analytics/pilot/rebuild', { from: today, to: today });
  await call('POST', '/api/admin/analytics/pilot/rebuild', { from: today, to: today });
  compare(await report(root, kiosk.id), e2, 'API, after the rollup ran twice');

  check(errors.length === 0, `no console error${errors.length ? `: ${errors.join(' | ')}` : ''}`);
} finally {
  await browser.close();
  if (account) await call('POST', `/api/admin/admins/${account.id}/active`, { isActive: false, reason: 'pilot check finished' }).catch(() => undefined);
  takeAway();
}

console.log(shotsDone.length ? `\nscreenshots: ${shotsDone.join(', ')}` : '');
console.log(failures.length === 0 ? '\nThe pilot report adds up.' : `\n${failures.length} thing(s) wrong.`);
process.exit(failures.length === 0 ? 0 : 1);
