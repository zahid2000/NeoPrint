// ---------------------------------------------------------------------------
// What an account that looks after some kiosks sees and hears in the panel,
// on the development stack (T-245, T-249).
//
//   docker compose up -d                       # the stack and a running kiosk
//   node e2e/scope.mjs                         # from admin/; KSK-9002 in neoprint-kshot
//
// An account is made for the run (a role with "see kiosks" and "see
// incidents", a random password, scoped to a kiosk that is NOT the watched
// one). Two panels stay open and are never reloaded - the development
// administrator's and the new account's - and then:
//
//   1. the watched kiosk's container is stopped and started: the administrator
//      hears its status and its incident; the scoped account hears nothing of
//      it and does not list it;
//   2. the account's scope is changed to the watched kiosk: the server ends
//      its live connection, the panel connects again by itself, and the kiosk
//      appears in its list;
//   3. the kiosk is paused and resumed: now the scoped account hears it -
//      and an order paid at that kiosk it does not: the payment and the queue
//      are for accounts that may read payments and orders (T-261);
//   4. the account is switched off: its connection is ended once more.
//
// Needs Chrome or Edge on the machine (playwright-core brings no browser).
// ---------------------------------------------------------------------------
import { execFileSync } from 'node:child_process';
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { launchBrowser } from './pages.mjs';

const base = process.env.ADMIN_URL ?? 'http://localhost:8080';
const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
// Roles and accounts are made here, which takes the development SuperAdmin.
const adminEmail = process.env.NEOPRINT_DEV_SUPERADMIN_EMAIL ?? 'dev-superadmin@neoprint.local';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const adminPassword = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const code = process.argv[2] ?? 'KSK-9002';
const container = process.argv[3] ?? 'neoprint-kshot';
const shots = process.argv[4] ? resolve(process.argv[4]) : null;

const started = Date.now();
const at = () => `${String(Math.round((Date.now() - started) / 1000)).padStart(4)}s`;
const say = (text) => console.log(`${at()}  ${text}`);
const failures = [];
const check = (ok, text) => {
  console.log(`${at()}  ${ok ? 'ok    ' : 'WRONG '} ${text}`);
  if (!ok) failures.push(text);
};

async function call(token, method, path, body) {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${token}`,
      'Content-Type': 'application/json',
      'Idempotency-Key': randomUUID(),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  if (!response.ok) {
    throw new Error(`${method} ${path} was refused: ${response.status} ${(await response.text()).slice(0, 200)}`);
  }

  return response.status === 204 ? null : response.json().catch(() => null);
}

async function signIn(email, password) {
  const response = await fetch(`${api}/api/auth/admin/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  });

  if (!response.ok) {
    throw new Error(`Could not sign in as ${email}: ${response.status}. Is the stack up, and a minute passed since the last ten sign-ins?`);
  }

  return (await response.json()).accessToken;
}

// --- the account of this run -------------------------------------------------
const root = await signIn(adminEmail, adminPassword);
const kiosks = await call(root, 'GET', '/api/admin/kiosks');
const watched = kiosks.find((kiosk) => kiosk.code === code);
const elsewhere = kiosks.find((kiosk) => kiosk.id !== watched?.id && kiosk.status !== 'Online') ?? kiosks.find((kiosk) => kiosk.id !== watched?.id);

if (!watched || !elsewhere) {
  console.error(`The stack needs ${code} and at least one other kiosk.`);
  process.exit(2);
}

const stamp = new Date().toISOString().replace(/\D/g, '').slice(0, 14);
const email = `scope-check-${stamp}@neoprint.local`;
const firstPassword = `A1!${randomBytes(12).toString('base64url')}`;
const password = `B2!${randomBytes(12).toString('base64url')}`;

const role = await call(root, 'POST', '/api/admin/roles', {
  name: `Scope check ${stamp}`,
  description: 'Made by admin/e2e/scope.mjs; safe to delete.',
  permissions: ['kiosks.view', 'incidents.view'],
});
const account = await call(root, 'POST', '/api/admin/admins', {
  email,
  fullName: 'Scope check',
  password: firstPassword,
  roleIds: [role.id],
  allKiosks: false,
  kioskIds: [elsewhere.id],
});

// A new account has to replace the password it was given before anything else.
await call(await signIn(email, firstPassword), 'POST', '/api/auth/admin/change-password', {
  currentPassword: firstPassword,
  newPassword: password,
});

say(`account ${email}: sees kiosks and incidents, scoped to ${elsewhere.code} (not ${code})`);

// --- two panels ---------------------------------------------------------------
const browser = await launchBrowser();

async function panel(name, who, secret) {
  const context = await browser.newContext({ viewport: { width: 1366, height: 900 }, locale: 'az' });
  const page = await context.newPage();
  const heard = [];
  const sockets = { opened: 0, closed: 0 };

  page.on('websocket', (socket) => {
    sockets.opened++;
    socket.on('close', () => sockets.closed++);
    socket.on('framereceived', ({ payload }) => {
      for (const text of String(payload).split('\u001e').filter(Boolean)) {
        try {
          const message = JSON.parse(text);
          if (message.type === 1) heard.push({ target: message.target, kioskId: message.arguments?.[0]?.kioskId ?? null, status: message.arguments?.[0]?.status });
        } catch {
          // Not a message of ours.
        }
      }
    });
  });

  await page.goto(`${base}/login`, { waitUntil: 'networkidle' });
  await page.locator('input[type=email]').fill(who);
  await page.locator('input[type=password]').fill(secret);
  await page.locator('button[type=submit]').click();
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });
  await page.goto(`${base}/kiosks`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(1500);

  return {
    name,
    page,
    heard,
    sockets,
    /** The kiosk codes in the list, with the status each shows. */
    rows: () =>
      page.evaluate(() =>
        [...document.querySelectorAll('tr.mat-mdc-row, tr[mat-row]')].map((row) => {
          const chip = row.querySelector('app-status-chip');
          const status = chip ? ([...chip.querySelectorAll('span')].at(-1)?.textContent.trim() ?? '') : '';
          return `${row.querySelector('strong')?.textContent.trim()}=${status}`;
        }),
      ),
    about: (kioskId, from = 0) => heard.slice(from).filter((event) => event.kioskId === kioskId),
  };
}

const everything = await panel('administrator', adminEmail, adminPassword);
const scoped = await panel('scoped account', email, password);

async function shot(name) {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  for (const each of [everything, scoped]) {
    await each.page.bringToFront();
    await each.page.screenshot({ path: join(shots, `${name}-${each.name.replace(/\s+/g, '-')}.png`) });
  }
}

const docker = (...args) => execFileSync('docker', args, { stdio: 'ignore' });
const wait = (ms) => everything.page.waitForTimeout(ms);

async function waitFor(what, seconds, condition) {
  const deadline = Date.now() + seconds * 1000;
  while (Date.now() < deadline) {
    if (await condition()) return true;
    await wait(2000);
  }
  console.log(`${at()}  (waited ${seconds} s for ${what})`);
  return false;
}

let failed = null;

try {
  // --- 1. somebody else's kiosk --------------------------------------------
  let rows = await scoped.rows();
  check(rows.length === 1 && rows[0].startsWith(`${elsewhere.code}=`), `the scoped account lists its own kiosk only: ${rows.join(', ') || '(nothing)'}`);
  check((await everything.rows()).some((row) => row.startsWith(`${code}=`)), `the administrator lists ${code}`);

  const incidents = await call(await signIn(email, password), 'GET', '/api/admin/incidents?pageSize=100');
  check(incidents.items.every((incident) => incident.kioskId === elsewhere.id), `GET admin/incidents as the scoped account: ${incidents.items.length} row(s), none of another kiosk`);
  await shot('s1-before');

  say(`docker stop ${container} - wait for the server to notice`);
  docker('stop', container);
  await waitFor('the offline event', 150, () => everything.about(watched.id).some((event) => event.target === 'IncidentRaised'));
  say(`docker start ${container}`);
  docker('start', container);
  await waitFor('the kiosk back', 120, () => everything.about(watched.id).some((event) => event.target === 'IncidentResolved'));
  await wait(3000);

  const mine = everything.about(watched.id).map((event) => event.target);
  check(['KioskStatusChanged', 'IncidentRaised', 'IncidentResolved'].every((target) => mine.includes(target)), `the administrator heard ${code}: ${[...new Set(mine)].join(', ')}`);
  check(scoped.about(watched.id).length === 0, `the scoped account heard nothing about ${code} (${scoped.heard.length} event(s) in all)`);
  rows = await scoped.rows();
  check(rows.every((row) => !row.startsWith(`${code}=`)), `and still does not list it: ${rows.join(', ')}`);
  await shot('s2-other-kiosk-went-and-came');

  // --- 2. the scope changes --------------------------------------------------
  const socketsBefore = { ...scoped.sockets };
  say(`PUT admins/${email}: scope -> ${code}`);
  await call(root, 'PUT', `/api/admin/admins/${account.id}`, {
    fullName: 'Scope check',
    roleIds: [role.id],
    allKiosks: false,
    kioskIds: [watched.id],
  });

  const reopened = await waitFor('the panel to connect again', 45, () => scoped.sockets.closed > socketsBefore.closed && scoped.sockets.opened > socketsBefore.opened);
  check(reopened, `the server ended the scoped panel's connection and the panel opened a new one (closed ${socketsBefore.closed} -> ${scoped.sockets.closed}, opened ${socketsBefore.opened} -> ${scoped.sockets.opened})`);

  const listed = await waitFor('the list to follow', 20, async () => (await scoped.rows()).some((row) => row.startsWith(`${code}=`)));
  rows = await scoped.rows();
  check(listed && rows.length === 1, `without a reload the scoped account now lists ${code} only: ${rows.join(', ')}`);
  await shot('s3-scope-changed');

  // --- 3. its own kiosk now ---------------------------------------------------
  const from = scoped.heard.length;
  say(`POST kiosks/${code}/pause, then resume`);
  await call(root, 'POST', `/api/admin/kiosks/${watched.id}/pause`);
  const sawPause = await waitFor('the pause in the scoped list', 20, async () => (await scoped.rows()).some((row) => row === `${code}=Servisdə`));
  await shot('s4-own-kiosk-paused');
  await call(root, 'POST', `/api/admin/kiosks/${watched.id}/resume`);
  const sawResume = await waitFor('the resume in the scoped list', 20, async () => (await scoped.rows()).some((row) => row === `${code}=Onlayn`));

  const statuses = scoped.about(watched.id, from).filter((event) => event.target === 'KioskStatusChanged').map((event) => event.status);
  check(statuses.includes('Maintenance') && statuses.includes('Online'), `the scoped account heard its kiosk: KioskStatusChanged ${statuses.join(', ')}`);
  check(sawPause && sawResume, `and its list followed without a reload (Servisdə, then Onlayn)`);

  // --- 3b. its own kiosk's money is still not its to hear (T-261) -------------
  // The account may see kiosks and incidents - not payments, not orders. An
  // order is placed and paid at its kiosk: the administrator hears the payment
  // and the queue, the scoped account neither.
  const adminFrom = everything.heard.length;
  const scopedFrom = scoped.heard.length;
  say(`an order is placed and paid at ${code} (tools/dev-kiosk/app-order.mjs)`);

  try {
    execFileSync('node', [join(import.meta.dirname, '..', '..', 'tools', 'dev-kiosk', 'app-order.mjs'), 'order', code], { stdio: 'pipe', timeout: 120_000 });
  } catch (error) {
    say(`the order tool said: ${String(error.stdout ?? error.message).trim().split('\n').at(-1)}`);
  }

  const paid = await waitFor('the payment in the administrator panel', 30, () =>
    everything.heard.slice(adminFrom).some((event) => event.target === 'PaymentSucceeded'),
  );
  const adminHeard = [...new Set(everything.heard.slice(adminFrom).map((event) => event.target))];
  check(paid, `the administrator heard the payment: ${adminHeard.join(', ') || '(nothing)'}`);

  const money = ['PaymentSucceeded', 'RefundStatusChanged', 'FiscalStatusChanged', 'QueueUpdated', 'DeviceRegistered', 'StorageHealthChanged'];
  const scopedHeard = [...new Set(scoped.heard.slice(scopedFrom).map((event) => event.target))];
  check(
    scopedHeard.every((target) => !money.includes(target)),
    `the scoped account, without payments.view and jobs.view, heard none of it: ${scopedHeard.join(', ') || '(nothing)'}`,
  );

  // --- 4. the account is switched off ----------------------------------------
  const socketsNow = { ...scoped.sockets };
  say(`POST admins/${email}/active: false`);
  await call(root, 'POST', `/api/admin/admins/${account.id}/active`, { isActive: false, reason: 'scope check finished' });
  const cut = await waitFor('the connection to end', 30, () => scoped.sockets.closed > socketsNow.closed);
  check(cut, 'the switched-off account lost its live connection');
} catch (error) {
  failed = error;
} finally {
  // Whatever happened, the account does not stay usable and the kiosk does not stay paused.
  await call(root, 'POST', `/api/admin/admins/${account.id}/active`, { isActive: false, reason: 'scope check finished' }).catch(() => {});
  await call(root, 'POST', `/api/admin/kiosks/${watched.id}/resume`).catch(() => {});
  try {
    docker('start', container);
  } catch {
    // Already running.
  }
  await browser.close();
}

if (failed) {
  console.error(`\nStopped: ${failed.message}`);
  process.exit(2);
}

console.log(failures.length === 0 ? '\nAn account hears and sees its own kiosks, and a change of scope reaches an open panel.' : `\n${failures.length} check(s) went wrong.`);
process.exit(failures.length === 0 ? 0 : 1);
