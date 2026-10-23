// ---------------------------------------------------------------------------
// The first DoD line of step 7A on the development stack (T-294, plan section
// 9.6): a printer that stops. With the Incident-lər page open in the panel, the
// fake printer of the Linux container kiosk KSK-9002 is made to fail:
//
//   A. in the middle of a print (PaperJam): the order is Interrupted, the kiosk
//      Paused at once, its screen says so, the panel hears a Critical incident
//      with a sound and a browser notification, the order's owner is offered
//      their money back - and when the printer is ready the order is printed
//      again and Completed;
//   B. before a print (OutOfPaper): the kiosk is Paused after its grace period,
//      the owner of the waiting order is offered their money back and takes it
//      (the refund goes to the wallet), and the incident closes by itself when
//      the printer is back.
//
// The guest's bank payment refunded by itself is e2e/pilot-report.mjs's third
// scenario; the API side of all of it is KioskOutageTests.
//
//   docker compose up -d && docker start neoprint-kshot
//   node e2e/outage.mjs                                  # from admin/
//   node e2e/outage.mjs ../docs/screenshots/step-7/t218
//
// The orders are a throwaway user's, never the seeded development accounts'.
// The printer is always made ready again at the end. Needs Chrome or Edge.
// ---------------------------------------------------------------------------
import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { throwawayUser } from './dev-data.mjs';
import { findRawKeys, launchBrowser } from './pages.mjs';

const base = process.env.ADMIN_URL ?? 'http://localhost:8080';
const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
const kioskCode = process.env.NEOPRINT_KIOSK ?? 'KSK-9002';
const container = process.env.NEOPRINT_KIOSK_CONTAINER ?? 'neoprint-kshot';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const shots = process.argv[2] ? resolve(process.argv[2]) : null;

const failures = [];
const check = (ok, text) => {
  console.log(`${ok ? 'ok    ' : 'WRONG '} ${text}`);
  if (!ok) failures.push(text);
};
const sleep = (ms) => new Promise((done) => setTimeout(done, ms));
const until = async (what, read, seconds = 90) => {
  const deadline = Date.now() + seconds * 1000;
  for (;;) {
    const value = await read();
    if (value) return value;
    if (Date.now() > deadline) throw new Error(`Waited ${seconds} s for ${what}.`);
    await sleep(2000);
  }
};

const spool = '/var/lib/neoprint/spool/printed';
const printer = (...command) =>
  execFileSync('docker', ['exec', container, 'sh', '-c', command.join(' ')], { encoding: 'utf8', env: { ...process.env, MSYS_NO_PATHCONV: '1' } });
const printerReady = () => printer(`rm -f ${spool}/printer-state.txt ${spool}/printer-fail-next.txt`);

const client = (token) => async (method, path, body, allow = []) => {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'Accept-Language': 'az', 'Idempotency-Key': randomUUID() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok && !allow.includes(response.status)) {
    throw new Error(`${method} ${path}: ${response.status} ${(await response.text()).slice(0, 200)}`);
  }
  return response.status === 204 ? null : response.json();
};

const admin = client(
  (await (await fetch(`${api}/api/auth/admin/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: 'dev-superadmin@neoprint.local', password }),
  })).json()).accessToken,
);
const kiosk = (await admin('GET', '/api/admin/kiosks')).find((k) => k.code === kioskCode);
if (!kiosk) throw new Error(`No ${kioskCode} on this stack.`);
const kioskState = async () => (await admin('GET', `/api/admin/kiosks/${kiosk.id}`)).status;
const openIncidents = async () => (await admin('GET', `/api/admin/incidents?status=Open&kioskId=${kiosk.id}`)).items;

check((await kioskState()) === 'Online', `${kioskCode} is online before the check`);

// A throwaway user with one ready one-page PDF and money in the wallet.
const person = await throwawayUser(api);
const user = client(person.token);
{
  const form = new FormData();
  form.append('file', new Blob([onePagePdf()], { type: 'application/pdf' }), 'qeza.pdf');
  form.append('isSensitive', 'false');
  const uploaded = await fetch(`${api}/api/files`, { method: 'POST', headers: { Authorization: `Bearer ${person.token}` }, body: form });
  if (!uploaded.ok) throw new Error(`upload: ${uploaded.status} ${await uploaded.text()}`);
}
const file = await until('the file to be ready', async () => ((await user('GET', '/api/files')).items ?? []).find((f) => f.status === 'Ready'));
await user('POST', '/api/wallet/top-up', { amount: 5 });
await until('the top-up', async () => (await user('GET', '/api/wallet')).balance >= 1);

const order = async () => {
  const session = await user('POST', '/api/sessions', { kiosk: kioskCode });
  const placed = await user('POST', `/api/sessions/${session.id}/print-jobs`, {
    fileId: file.id,
    options: { copies: 1, pageRange: null, color: false, duplex: false, paper: 'A4', orientation: 'Auto', pagesPerSheet: 1, scaling: 'Fit' },
  });
  await user('POST', `/api/payments/${placed.paymentId}/pay-with-app`, { source: 'Wallet', useBonus: false });
  return placed.jobId;
};
const mine = async (jobId) => (await user('GET', '/api/print-jobs/my')).find((job) => job.id === jobId);
const offered = async (type) => ((await user('GET', '/api/notifications')).items ?? []).some((item) => item.type === type);

// The panel: Incident-lər, open, with notifications allowed - and what it plays and shows written down.
const browser = await launchBrowser();
const context = await browser.newContext({ viewport: { width: 1366, height: 900 }, locale: 'az', serviceWorkers: 'block' });
await context.grantPermissions(['notifications'], { origin: base });
await context.addInitScript(() => {
  window.__tones = 0;
  window.__notices = [];
  const Audio = window.AudioContext;
  if (Audio) {
    const make = Audio.prototype.createOscillator;
    Audio.prototype.createOscillator = function () {
      window.__tones++;
      return make.call(this);
    };
  }
  const Shown = window.Notification;
  if (Shown) {
    window.Notification = class extends Shown {
      constructor(title, options) {
        window.__notices.push(title);
        super(title, options);
      }
    };
  }
});
const page = await context.newPage();
const errors = [];
page.on('console', (m) => m.type() === 'error' && !/Failed to load resource/.test(m.text()) && errors.push(m.text().slice(0, 200)));
page.on('pageerror', (e) => errors.push(String(e).slice(0, 200)));

const shot = async (name) => {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  await page.waitForTimeout(500);
  await page.screenshot({ path: join(shots, `${name}.png`), fullPage: true });
};
const kioskScreen = (name) => {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  const picture = execFileSync('docker', ['exec', container, 'sh', '-c', 'DISPLAY=:99 import -window root png:-'], { maxBuffer: 64 * 1024 * 1024 });
  writeFileSync(join(shots, `${name}.png`), picture);
};

try {
  await page.goto(`${base}/login`, { waitUntil: 'networkidle' });
  await page.locator('input[type=email]').fill('dev-superadmin@neoprint.local');
  await page.locator('input[type=password]').fill(password);
  await page.locator('button[type=submit]').click();
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });
  await page.goto(`${base}/incidents`, { waitUntil: 'networkidle' });
  await page.locator('main h1').first().waitFor({ timeout: 15_000 });

  // --- A: the printer jams in the middle of the print --------------------------------------
  console.log('\nA. PaperJam in the middle of a print');
  printer(`echo PaperJam > ${spool}/printer-fail-next.txt`);
  const first = await order();

  const interrupted = await until('the order to stop', async () => {
    const job = await mine(first);
    return job?.status === 'Interrupted' && job;
  }, 60).catch(() => null);
  check(!!interrupted, 'A: the order stopped at the printer is Interrupted');
  check(!!interrupted?.canCancel && !!interrupted?.kioskStopped, 'A: its owner may cancel it and is told the kiosk has stopped');
  check((await kioskState()) === 'Paused', 'A: the kiosk is Paused at once');

  const row = page.locator('tr.mat-mdc-row', { hasText: kioskCode }).filter({ hasText: /sıxışıb/ }).first();
  // It closes by itself at the next heartbeat, when the printer is ready: caught while it is there.
  await row.waitFor({ timeout: 30_000 })
    .then(async () => {
      check(true, 'A: the Critical incident shows on the open Incident-lər page without a reload');
      await shot('01-incident-arrives-with-sound');
    })
    .catch(() => check(false, 'A: the incident did not show on the open page'));
  const heard = await page.evaluate(() => ({ tones: window.__tones, notices: [...window.__notices] }));
  check(heard.tones > 0, `A: the panel played its sound (${heard.tones} tones)`);
  check(heard.notices.length > 0, `A: the browser was given a notification ("${heard.notices[0] ?? ''}")`);
  kioskScreen('02-kiosk-stopped-paper-jam');

  check(await until('the refund offer', () => offered('JobInterruptedRefundOffer'), 30).catch(() => false),
    'A: the owner is offered the money back (JobInterruptedRefundOffer)');

  // The fault was one print: at the next heartbeat the printer is ready and the order is printed again.
  const done = await until('the order to be printed again', async () => {
    const job = await mine(first);
    return job?.status === 'Completed' && job;
  }, 120).catch(() => null);
  check(!!done, 'A: when the printer is ready the order is printed again and Completed');
  check((await kioskState()) === 'Online', 'A: the kiosk is Online again');

  // --- B: the printer stops before a print ----------------------------------------------------
  console.log('\nB. OutOfPaper before a print');
  printer(`echo OutOfPaper > ${spool}/printer-state.txt`);
  const second = await order();

  check(await until('the kiosk to pause', async () => (await kioskState()) === 'Paused', 120).catch(() => false),
    'B: after the grace period the kiosk is Paused');
  const outage = await until('the PaperOut incident', async () => (await openIncidents()).find((i) => i.type === 'PaperOut'), 30).catch(() => null);
  check(outage?.severity === 'Critical', 'B: a Critical PaperOut incident is open');
  const waiting = await mine(second);
  check(waiting?.status === 'Queued' && waiting.canCancel && waiting.kioskStopped, 'B: the waiting order is kept, its owner may cancel it');
  check(await until('the refund offer', () => offered('KioskOutOfServiceRefundOffer'), 30).catch(() => false),
    'B: the owner is offered the money back (KioskOutOfServiceRefundOffer)');
  kioskScreen('03-kiosk-stopped-out-of-paper');
  await shot('04-incident-paper-out');

  const before = (await user('GET', '/api/wallet')).balance;
  await user('POST', `/api/print-jobs/my/${second}/cancel`);
  const after = await until('the refund to the wallet', async () => {
    const balance = (await user('GET', '/api/wallet')).balance;
    return balance > before && balance;
  }, 30).catch(() => null);
  check(after !== null, `B: the owner took it: the refund went to the wallet (${before} → ${after})`);

  printerReady();
  check(await until('the kiosk to be back', async () => (await kioskState()) === 'Online', 90).catch(() => false),
    'B: the printer is back: the kiosk is Online');
  check(await until('the incident to close', async () => !(await openIncidents()).some((i) => i.type === 'PaperOut'), 30).catch(() => false),
    'B: the incident closed by itself');
  kioskScreen('05-kiosk-back');

  const raw = await page.evaluate(findRawKeys);
  check(!raw.length, `the Incident-lər page shows no raw key${raw.length ? ` (${raw.join(', ')})` : ''}`);
  check(!errors.length, `no console error${errors.length ? `: ${errors.join(' | ')}` : ''}`);
} finally {
  printerReady();
  await browser.close();
}

console.log(failures.length ? `\n${failures.length} wrong` : '\nall as expected');
process.exit(failures.length ? 1 : 0);

/** A valid one-page A4 PDF with a line of text, made here: the repository holds no PDF to upload. */
function onePagePdf() {
  const text = 'BT /F1 24 Tf 72 720 Td (NeoPrint 7A DoD) Tj ET';
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>',
    `<< /Length ${text.length} >>\nstream\n${text}\nendstream`,
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
  ];
  let pdf = '%PDF-1.4\n';
  const offsets = objects.map((body, index) => {
    const at = pdf.length;
    pdf += `${index + 1} 0 obj\n${body}\nendobj\n`;
    return at;
  });
  const xref = pdf.length;
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n${offsets.map((o) => `${String(o).padStart(10, '0')} 00000 n \n`).join('')}`;
  pdf += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(pdf, 'latin1');
}
