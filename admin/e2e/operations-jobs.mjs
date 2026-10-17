// ---------------------------------------------------------------------------
// The system's own checks on the development stack (step 7A, M11 - T-215):
// data broken on purpose - a balance its ledger does not add up to, a message
// in a dead-letter queue, an object no file points at, a paid order cancelled
// with no refund - and each check, started from the Hangfire panel the way an
// administrator would, ends in the incident or the deletion expected; the
// panel's actions are in the audit log. Pictures of the panel, the
// "Arxa plan işləri" page (desk and phone) and the incidents.
//
//   docker compose up -d
//   node e2e/operations-jobs.mjs                         # from admin/
//   node e2e/operations-jobs.mjs ../docs/screenshots/step-7/t215
//
// Repeatable (T-266): everything it makes goes at the end - the kiosk and its
// money, the wallet, the queue; its incidents are closed; two settings it
// changes for a minute are put back. The audit log keeps what happened.
// Needs Chrome or Edge on the machine (playwright-core brings no browser).
// ---------------------------------------------------------------------------
import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { kioskSession, makeKiosk, purgeKiosk } from './dev-data.mjs';
import { launchBrowser } from './pages.mjs';

const base = process.env.ADMIN_URL ?? 'http://localhost:8080';
const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
const rabbit = process.env.NEOPRINT_RABBIT_MANAGEMENT ?? 'http://localhost:15672';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const shots = process.argv[2] ? resolve(process.argv[2]) : null;

const failures = [];
const check = (ok, text) => {
  console.log(`${ok ? 'ok    ' : 'WRONG '} ${text}`);
  if (!ok) failures.push(text);
};

const until = async (what, test, seconds = 60) => {
  const end = Date.now() + seconds * 1000;
  for (;;) {
    const value = await test();
    if (value) return value;
    if (Date.now() > end) throw new Error(`${what}: not within ${seconds} s`);
    await new Promise((r) => setTimeout(r, 1000));
  }
};

async function signIn(email) {
  for (let tries = 0; ; tries++) {
    const response = await fetch(`${api}/api/auth/admin/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password }),
    });
    if (response.status === 429 && tries < 2) {
      console.log('       (too many sign-ins in a minute: waiting)');
      await new Promise((r) => setTimeout(r, 61_000));
      continue;
    }
    if (!response.ok) throw new Error(`sign-in: ${response.status}`);
    return (await response.json()).accessToken;
  }
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

/** The development database, through its container (docs/DEV_ENVIRONMENT.md section 3). */
const mongo = (script) =>
  execFileSync('docker', ['exec', 'neoprint-mongo', 'mongosh', '-u', 'neoprint', '-p', 'devMongoPassword', '--authenticationDatabase', 'admin', 'neoprint', '--quiet', '--eval', script], {
    encoding: 'utf8',
    env: { ...process.env, MSYS_NO_PATHCONV: '1' },
  }).trim();

/** The broker's management API, with the account the container was started with - read from it, written nowhere. */
const brokerAuth = () => {
  const env = (name) => execFileSync('docker', ['exec', 'neoprint-rabbitmq', 'printenv', name], { encoding: 'utf8' }).trim();
  return `Basic ${Buffer.from(`${env('RABBITMQ_DEFAULT_USER')}:${env('RABBITMQ_DEFAULT_PASS')}`).toString('base64')}`;
};
const auth = brokerAuth();
const broker = async (method, path, body) => {
  const response = await fetch(`${rabbit}/${path}`, {
    method,
    headers: { Authorization: auth, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok && response.status !== 404) throw new Error(`broker ${method} ${path}: ${response.status}`);
  return response.status === 204 || response.status === 201 || response.status === 404 ? null : response.json();
};

const settings = async () => Object.fromEntries((await call('GET', '/api/admin/settings')).map((s) => [s.key, s.value]));
const setSettings = (values) => call('PUT', '/api/admin/settings', { values });

// ---------------------------------------------------------------------------
// The broken data
// ---------------------------------------------------------------------------

const queue = `e2e-check-${Date.now() % 100000}_error`;
const walletUser = randomUUID();
const kiosk = await makeKiosk(call, 'E2E jobs');
const before = await settings();
const keep = { 'Files.CleanupDryRun': before['Files.CleanupDryRun'] ?? 'true', 'Files.CleanupGraceHours': before['Files.CleanupGraceHours'] ?? '24' };
let lostPayment = null;
let orphan = null;

let tidied = false;
const tidy = () => {
  if (tidied) return;
  tidied = true;
  mongo(`db.wallets.deleteOne({ _id: UUID("${walletUser}") }); print('ok')`);
  purgeKiosk(api, kiosk.id, [kiosk.deviceId]);
};
process.on('exit', tidy);

try {
  // 1. A balance of 5 ₼ with no ledger line behind it.
  mongo(`db.wallets.insertOne({ _id: UUID("${walletUser}"), CreatedAt: new Date(), RowVersion: 0, Balance: { amount: Decimal128("5.00"), currency: "AZN" }, BonusPages: 0 }); print('ok')`);

  // 2. A message that failed every retry, three hours ago.
  await broker('PUT', `api/queues/%2F/${queue}`, { durable: false, auto_delete: false });
  await broker('POST', 'api/exchanges/%2F/amq.default/publish', {
    properties: { headers: { 'MT-Fault-Timestamp': new Date(Date.now() - 3 * 3_600_000).toISOString() } },
    routing_key: queue,
    payload: '{}',
    payload_encoding: 'string',
  });
  await until('the broker to count the message', async () => (await broker('GET', `api/queues/%2F/${queue}`))?.messages === 1, 30);

  // 3. An object no file points at.
  const made = await fetch(`${api}/api/dev/storage/orphan`, { method: 'POST' });
  orphan = (await made.json()).key;

  // 4. A paid order cancelled behind the system's back, two hours ago, with no refund.
  await call('POST', `/api/admin/kiosks/${kiosk.id}/paper/refill`, { paper: 'A4', mode: 'Exact', sheets: 500 });
  const asKiosk = await kioskSession(api, kiosk.deviceId, kiosk.privateKey);
  await asKiosk('POST', '/api/kiosks/heartbeat', { appVersion: '0.7.0', agentVersion: '0.7.0', printerOnline: true });
  const job = await asKiosk('POST', '/api/print-jobs', {
    localFileName: 'e2e.pdf',
    pageCount: 1,
    options: { copies: 1, pageRange: null, color: false, duplex: false, paper: 'A4', orientation: 'Auto', pagesPerSheet: 1, scaling: 'Fit' },
  });
  const payment = await asKiosk('POST', '/api/payments/intents', { jobId: job.id, method: 'BankQr' });
  await fetch(`${api}/api/dev/payments/${payment.id}/simulate-success`, { method: 'POST' });
  await until('the payment and its receipt', () =>
    mongo(`print(db.payments.countDocuments({ _id: UUID("${payment.id}"), Status: "Succeeded" }) + db.fiscal_receipts.countDocuments({ PaymentId: UUID("${payment.id}") }))`) === '2');
  mongo(`db.print_jobs.updateOne({ _id: UUID("${job.id}") }, { $set: { Status: "Cancelled" } }); db.payments.updateOne({ _id: UUID("${payment.id}") }, { $set: { PaidAt: new Date(Date.now() - 2 * 3600 * 1000) } }); print('ok')`);
  lostPayment = payment.id;
} catch (error) {
  tidy();
  throw error;
}

console.log(`broken: wallet ${walletUser.slice(0, 8)}, queue ${queue}, object …${orphan.slice(-30)}, payment ${lostPayment.slice(0, 8)}`);

// ---------------------------------------------------------------------------
// The panel: start each check as an administrator would
// ---------------------------------------------------------------------------

const browser = await launchBrowser();
const errors = [];
const watch = (page) => page.on('console', (m) => m.type() === 'error' && !/Failed to load resource/.test(m.text()) && errors.push(m.text().slice(0, 200)));
const shotsDone = [];
const shot = async (page, name) => {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  await page.waitForTimeout(400);
  await page.screenshot({ path: join(shots, `${name}.png`), fullPage: true });
  shotsDone.push(name);
};

const lastRun = async (job) => (await call('GET', '/api/admin/system/jobs')).runs[job];

try {
  const context = await browser.newContext({ viewport: { width: 1366, height: 900 }, locale: 'az', serviceWorkers: 'block' });
  const desk = await context.newPage();
  watch(desk);
  await desk.goto(`${base}/login`, { waitUntil: 'networkidle' });
  await desk.locator('input[type=email]').fill('dev-superadmin@neoprint.local');
  await desk.locator('input[type=password]').fill(password);
  await desk.locator('button[type=submit]').click();
  await desk.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });

  await desk.goto(`${base}/system-jobs`, { waitUntil: 'networkidle' });
  await desk.locator('[data-job="dead-letters"]').waitFor();

  // The panel opens in its own tab, with its ticket.
  const [panel] = await Promise.all([context.waitForEvent('page'), desk.locator('.jobs__hangfire').click()]);
  await panel.waitForURL(/\/api\/admin\/hangfire/, { timeout: 15_000 });
  await panel.goto(`${base}/api/admin/hangfire/recurring?count=100`, { waitUntil: 'networkidle' });
  check((await panel.locator('body').innerText()).includes('file-cleanup'), 'Hangfire: the recurring jobs, the new checks among them');

  const trigger = async (id) => {
    const row = panel.locator('tr', { has: panel.locator(`input[value="${id}"]`) });
    await row.locator('input[type=checkbox]').check();
    await panel.locator('button.js-jobs-list-command', { hasText: /Trigger now/i }).click();
    await panel.waitForLoadState('networkidle');
    await row.locator('input[type=checkbox]').uncheck().catch(() => undefined);
  };
  const runOf = async (job, id, since) => {
    await trigger(id);
    return until(`${job} to run`, async () => {
      const run = await lastRun(job);
      return run && new Date(run.finishedAt) >= since ? run : null;
    }, 90);
  };

  // Dead letters.
  let since = new Date();
  const dlq = await runOf('dead-letters', 'dead-letter-monitor', since);
  const seen = dlq.queues?.find((q) => q.name === queue);
  check(seen?.messages === 1, `DLQ: the queue holds 1 message (${seen?.messages})`);
  check(!!seen?.oldestAt && Math.abs(Date.now() - 3 * 3_600_000 - Date.parse(seen.oldestAt)) < 120_000, 'DLQ: its age is about 3 hours, from the fault header');
  const dlqIncident = mongo(`print(JSON.stringify(db.incidents.findOne({ DedupKey: "dead-letter:${queue}" }, { Severity: 1, Open: 1 })))`);
  check(/"Severity":"High"/.test(dlqIncident) && /"Open":true/.test(dlqIncident), `DLQ: a High incident is open (${dlqIncident})`);

  // Wallet ledger.
  since = new Date();
  const wallet = await runOf('wallet-ledger', 'wallet-ledger-reconciliation', since);
  check(wallet.findings.some((f) => f.ref === walletUser && f.amount === 5 && f.other === 0), 'ledger: the 5 ₼ balance with an empty ledger is found');
  const walletIncident = mongo(`print(JSON.stringify(db.incidents.findOne({ DedupKey: "wallet-mismatch:${walletUser}" }, { Severity: 1, Open: 1 })))`);
  check(/"Severity":"Critical"/.test(walletIncident), `ledger: a Critical incident (${walletIncident})`);
  check(mongo(`print(db.wallets.findOne({ _id: UUID("${walletUser}") }).Balance.amount.toString())`) === '5.00', 'ledger: the balance was not touched');

  // Payments.
  since = new Date();
  const money = await runOf('payment-consistency', 'payment-consistency', since);
  check(money.findings.some((f) => f.kind === 'paidNotPrinted' && f.ref === lostPayment), 'payments: the paid, cancelled, unrefunded order is found');
  const moneyIncident = mongo(`print(JSON.stringify(db.incidents.findOne({ DedupKey: "money-check:paidNotPrinted:${lostPayment}" }, { Severity: 1, KioskId: 1 })))`);
  check(/"Severity":"Critical"/.test(moneyIncident), `payments: a Critical incident at the kiosk (${moneyIncident.slice(0, 120)})`);
  check(mongo(`print(db.refunds.countDocuments({ PaymentId: UUID("${lostPayment}") }))`) === '0', 'payments: nothing was refunded automatically');

  // Run twice: the same, nothing doubled.
  since = new Date();
  await runOf('payment-consistency', 'payment-consistency', since);
  check(mongo(`print(db.incidents.countDocuments({ DedupKey: "money-check:paidNotPrinted:${lostPayment}" }))`) === '1', 'payments: run again, still one incident');

  // File cleanup: a trial first, then for real (the object is new: the grace is set to 0 for a minute).
  await setSettings({ 'Files.CleanupGraceHours': '0', 'Files.CleanupDryRun': 'true' });
  since = new Date();
  const dry = await runOf('file-cleanup', 'file-cleanup', since);
  check(dry.dryRun && dry.findings.some((f) => f.kind === 'wouldDelete' && f.ref === orphan), 'files: the trial lists the object and deletes nothing');
  // Leftovers this check made (now or in a run that stopped half-way) are named dev-leftover.pdf; anything else is not its to delete.
  const own = (key) => key.endsWith('/dev-leftover.pdf');
  const others = dry.findings.filter((f) => !own(f.ref)).length + Math.max(0, (dry.counts.deleted ?? 0) - dry.findings.length);

  if (others === 0) {
    await setSettings({ 'Files.CleanupDryRun': 'false' });
    since = new Date();
    const real = await runOf('file-cleanup', 'file-cleanup', since);
    check(real.findings.some((f) => f.kind === 'deleted' && f.ref === orphan), 'files: deleted for real');
    check(mongo(`print(db.audit_logs.countDocuments({ Action: "files.cleanup.deleted", Before: "${orphan}" }))`) === '1', 'files: the deletion is in the audit log');
    since = new Date();
    const again = await runOf('file-cleanup', 'file-cleanup', since);
    check(!again.findings.some((f) => f.ref === orphan), 'files: run again, nothing more of it');
  } else {
    // Other leftovers on this machine's store: not this check's to delete.
    console.log(`       (${others} other unused object(s) in the development store: the real run is left to the administrator)`);
  }

  await setSettings(keep);

  // What was done in the panel is in the audit log.
  const audit = (await call('GET', '/api/admin/audit-logs?action=hangfire.&pageSize=50')).items;
  check(audit.some((e) => e.action === 'hangfire.opened'), 'audit: the panel was opened');
  check(audit.some((e) => e.action === 'hangfire.action' && e.entityId === 'payment-consistency'), 'audit: "Trigger now" of payment-consistency');
  // The panel's own sticky bar would land mid-picture in a full-page shot: the window only.
  if (shots) {
    mkdirSync(shots, { recursive: true });
    await panel.locator('input[value="payment-consistency"]').scrollIntoViewIfNeeded();
    await panel.screenshot({ path: join(shots, '01-hangfire-recurring.png') });
    shotsDone.push('01-hangfire-recurring');
  }

  // The page, on a desk and a phone.
  await desk.goto(`${base}/system-jobs`, { waitUntil: 'networkidle' });
  await desk.locator('[data-job="payment-consistency"] app-status-chip').waitFor();
  check((await desk.locator('[data-job="wallet-ledger"]').innerText()).includes('5,00'), 'page: the balance mismatch is listed');
  check((await desk.locator('[data-job="payment-consistency"] [data-metric="paidNotPrinted"]').innerText()).includes('1'), 'page: the money finding is counted');
  check((await desk.locator('[data-job="dead-letters"]').innerText()).includes(queue), 'page: the failing queue is listed');
  await shot(desk, '02-system-jobs-desk');

  await desk.goto(`${base}/incidents`, { waitUntil: 'networkidle' });
  await desk.locator('tr.mat-mdc-row').first().waitFor();
  await shot(desk, '03-incidents');

  const phone = await (await browser.newContext({ viewport: { width: 390, height: 844 }, locale: 'az', isMobile: true, hasTouch: true, serviceWorkers: 'block' })).newPage();
  watch(phone);
  await phone.goto(`${base}/login`, { waitUntil: 'networkidle' });
  await phone.locator('input[type=email]').fill('dev-superadmin@neoprint.local');
  await phone.locator('input[type=password]').fill(password);
  await phone.locator('button[type=submit]').click();
  await phone.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });
  await phone.goto(`${base}/system-jobs`, { waitUntil: 'networkidle' });
  await phone.locator('[data-job="dead-letters"]').waitFor();
  check(await phone.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), 'phone: no sideways scroll at 390 px');
  await shot(phone, '04-system-jobs-phone');

  // The queue emptied: the check closes its incident by itself.
  await broker('DELETE', `api/queues/%2F/${queue}/contents`);
  await until('the broker to count it empty', async () => (await broker('GET', `api/queues/%2F/${queue}`))?.messages === 0, 30);
  since = new Date();
  await runOf('dead-letters', 'dead-letter-monitor', since);
  check(
    /"Open":false/.test(mongo(`print(JSON.stringify(db.incidents.findOne({ DedupKey: "dead-letter:${queue}" }, { Open: 1 })))`)),
    'DLQ: the empty queue closed its incident',
  );


  check(errors.length === 0, `no console error${errors.length ? `: ${errors.join(' | ')}` : ''}`);
} finally {
  await browser.close();
  await setSettings(keep).catch(() => undefined);
  await broker('DELETE', `api/queues/%2F/${queue}`).catch(() => undefined);
  // The wallet's incident has no kiosk: closed here, with what was done.
  const open = mongo(`print(JSON.stringify(db.incidents.find({ DedupKey: { $in: ["wallet-mismatch:${walletUser}", "dead-letter:${queue}"] }, Open: true }, { _id: 1 }).toArray().map(i => i._id.toString().replace(/UUID\\("|"\\)/g, ""))))`);
  for (const id of JSON.parse(open || '[]')) {
    await call('POST', `/api/admin/incidents/${id}/resolve`, { note: 'E2E: arxa plan işlərinin yoxlaması — test məlumatı silindi' }).catch(() => undefined);
  }
  tidy();
}

console.log(shotsDone.length ? `\nscreenshots: ${shotsDone.join(', ')}` : '');
console.log(failures.length === 0 ? '\nEvery check found what was broken.' : `\n${failures.length} thing(s) wrong.`);
process.exit(failures.length === 0 ? 0 : 1);
