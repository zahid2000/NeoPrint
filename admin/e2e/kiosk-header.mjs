#!/usr/bin/env node
// ---------------------------------------------------------------------------
// The Linux container kiosk's header, on the stack (T-170, T-216):
//
//   A. Phones joined to KSK-9002 (neoprint-kshot) are still counted after the
//      kiosk restarts: it asks the server, it does not wait to hear again.
//   B. A kiosk whose device is detached shows nothing of that kiosk on the
//      activation screen. Done with a second, temporary container kiosk -
//      neoprint-kshot itself is never detached.
//
// Brings its own users, kiosk and container, and removes them at the end. The
// kiosk image must hold the current code: bash tools/dev-kiosk/build-linux-kiosk.sh.
//
//   node e2e/kiosk-header.mjs                                   # from admin/
//   node e2e/kiosk-header.mjs ../docs/screenshots/step-7/t216   # + screenshots
// ---------------------------------------------------------------------------
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { purgeDevices, purgeKiosk, purgeUser, throwawayUser } from './dev-data.mjs';

const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
const kioskCode = process.env.NEOPRINT_KIOSK_CODE ?? 'KSK-9002';
const kioskContainer = 'neoprint-kshot';
const spare = 'neoprint-kshot-m12';
const shots = process.argv[2] ? resolve(process.argv[2]) : null;
const env = { ...process.env, MSYS_NO_PATHCONV: '1' };

let failed = 0;
const check = (ok, label) => {
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${label}`);
  if (!ok) failed++;
};
const wait = (ms) => new Promise((done) => setTimeout(done, ms));
const docker = (...args) => execFileSync('docker', args, { encoding: 'utf8', env, stdio: ['ignore', 'pipe', 'pipe'] }).trim();

async function until(read, done, what, seconds = 60) {
  for (let i = 0; i < seconds; i++) {
    const value = await read();
    if (done(value)) return value;
    await wait(1000);
  }
  throw new Error(`Timed out waiting for ${what}.`);
}

/** The container's screen, saved when screenshots were asked for. */
function shot(container, name) {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  docker('exec', container, 'sh', '-c', 'import -window root /tmp/shot.png');
  docker('cp', `${container}:/tmp/shot.png`, join(shots, `${name}.png`));
  console.log(`     screenshot ${name}.png`);
}

/** Whether the kiosk process has its window up again. */
function windowUp(container) {
  try {
    return docker('exec', container, 'sh', '-c', 'xdotool search --name "^NeoPrint$" | head -1') !== '';
  } catch {
    return false;
  }
}

const login = await fetch(`${api}/api/auth/admin/login`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    email: process.env.NEOPRINT_DEV_SUPERADMIN_EMAIL ?? 'dev-superadmin@neoprint.local',
    password: process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026',
  }),
});
if (!login.ok) throw new Error(`Admin sign-in: ${login.status}`);
const adminToken = (await login.json()).accessToken;

async function call(method, path, body, token = adminToken) {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'Accept-Language': 'az', 'Idempotency-Key': crypto.randomUUID() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${text.slice(0, 200)}`);
  return text ? JSON.parse(text) : null;
}

const users = [];
let spareKiosk;
let spareDevice;

try {
  // ---- A. Phones after a restart -------------------------------------------
  const kiosk = (await call('GET', '/api/admin/kiosks')).find((k) => k.code === kioskCode);
  check(kiosk?.status === 'Online', `${kioskCode} is online (${kiosk?.status})`);

  for (let i = 0; i < 2; i++) users.push(await throwawayUser(api));
  const sessions = [];
  for (const user of users) sessions.push(await call('POST', '/api/sessions', { kiosk: kioskCode }, user.token));
  await wait(3000);
  shot(kioskContainer, '01-kiosk-two-phones');

  const sinceRestart = new Date().toISOString();
  docker('restart', kioskContainer);
  await until(async () => windowUp(kioskContainer), Boolean, 'the kiosk window after the restart');
  // The kiosk catches up (configuration, commands, queue, sessions) once it has signed in.
  await until(
    async () => docker('logs', '--since', sinceRestart, 'neoprint-api').includes('/api/kiosks/me/sessions') || null,
    Boolean,
    'the kiosk asking for its sessions',
    90,
  ).then(
    () => check(true, 'after the restart the kiosk asked the server for its phones'),
    () => check(false, 'after the restart the kiosk asked the server for its phones'),
  );
  await wait(4000);
  shot(kioskContainer, '02-kiosk-after-restart-two-phones');

  const open = await fetch(`${api}/api/sessions/current`, { headers: { Authorization: `Bearer ${users[0].token}` } });
  check(open.status === 200, 'the phones are still joined on the server (no QR scanned again)');

  for (const [i, user] of users.entries()) await call('DELETE', `/api/sessions/${sessions[i].id}`, undefined, user.token);
  await wait(3000);
  shot(kioskContainer, '03-kiosk-phones-gone');

  // ---- B. A detached kiosk forgets its name ----------------------------------
  docker('rm', '-f', spare);
  try { docker('volume', 'rm', `${spare}-data`); } catch { /* none yet */ }
  docker('run', '-d', '--name', spare, '--hostname', 'm12-spare-kiosk', '--network', 'neoprint_default', '--tmpfs', '/media/kiosk/NEOSTICK:size=20m',
    '-v', `${spare}-data:/var/lib/neoprint`, kioskContainer, '--api-url', 'http://neoprint-api:8080');
  const hostname = docker('inspect', '-f', '{{.Config.Hostname}}', spare);

  const device = await until(
    async () => (await call('GET', '/api/admin/devices?status=Pending')).find((d) => d.hostname === hostname),
    Boolean,
    'the spare kiosk to register',
    90,
  );
  spareDevice = device.id;
  const activated = await call('POST', `/api/admin/devices/${device.id}/activate`, {
    newKiosk: {
      name: 'M12 müvəqqəti kiosk',
      lat: 40.3793,
      lng: 49.8486,
      address: 'Bakı (e2e)',
      defaultLanguage: 'az',
      paperCapacityA4: 500,
      paperCapacityA3: 0,
      adminPin: String(100000 + Math.floor(Math.random() * 899999)),
    },
  });
  spareKiosk = activated.kioskId;

  // Signed in and configured: it has said hello with a heartbeat.
  await until(
    async () => (await call('GET', `/api/admin/kiosks/${spareKiosk}`)).lastHeartbeatAt ?? null,
    Boolean,
    'the spare kiosk to come online',
    120,
  );
  await wait(5000);
  shot(spare, '04-spare-kiosk-with-its-name');

  const sinceRevoke = new Date().toISOString();
  await call('POST', `/api/admin/devices/${spareDevice}/revoke`, { reason: 'T-216 check' });
  await until(
    async () => /detached; wiping the spool/.test(docker('logs', '--since', sinceRevoke, spare)) || null,
    Boolean,
    'the spare kiosk to notice it was detached',
    120,
  ).then(
    () => check(true, 'the detached kiosk wiped its spool and went back to activation'),
    () => check(false, 'the detached kiosk wiped its spool and went back to activation'),
  );
  await wait(5000);
  shot(spare, '05-spare-kiosk-detached-no-old-name');
  console.log('     (the header on the activation screen is checked on the screenshot and by the kiosk test "A_detached_kiosk_shows_nothing...")');
} finally {
  for (const user of users) purgeUser(api, user.id);
  try { docker('rm', '-f', spare); } catch { /* not started */ }
  try { docker('volume', 'rm', `${spare}-data`); } catch { /* not made */ }
  if (spareKiosk) purgeKiosk(api, spareKiosk, spareDevice ? [spareDevice] : []);
  else if (spareDevice) purgeDevices(api, [spareDevice]);
}

console.log(failed ? `\n${failed} check(s) failed` : '\nall checks passed');
process.exit(failed ? 1 : 0);
