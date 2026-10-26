#!/usr/bin/env node
// ---------------------------------------------------------------------------
// A golden image and two of its clones, on the development stack (T-327):
// each clone makes an identity of its own and activates itself as a kiosk of
// its own with one enrollment token made for two - nobody reads a screen.
//
//   node tools/dev-kiosk/golden-image.mjs                       # makes its own token through the API
//   node tools/dev-kiosk/golden-image.mjs --token-file <file>   # a token made in the panel (admin/e2e/enrollment.mjs)
//
// 1. The master: the Linux container kiosk (build-linux-kiosk.sh) starts with
//    no token, makes a device key and registers - as a machine does before it
//    is captured. prepare-image.sh cleans it and its --check agrees.
// 2. The image: the master's file system and data, copied twice (a disk
//    image written onto two machines).
// 3. Each clone boots (neoprint-firstboot first), makes its own device key,
//    is given the token with neoprint-enroll and starts again: activated.
// 4. Checked through the API: two devices, two kiosks, two codes, the token
//    used 2 of 2, the master's old device never activated; on the clones the
//    keys and machine ids differ and the token file is gone.
//
// The clones' devices are revoked at the end (their kiosks stay, NotActivated)
// and the containers, volumes and image removed. Run from Git Bash, WSL or
// Linux with the compose stack up. The token is never printed.
// ---------------------------------------------------------------------------
import { execFileSync } from 'node:child_process';
import { chmodSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const master = 'neoprint-golden';
const clones = ['neoprint-golden-1', 'neoprint-golden-2'];
const image = 'neoprint-golden-image:captured';
const env = { ...process.env, MSYS_NO_PATHCONV: '1' };

const failures = [];
const check = (ok, text) => {
  console.log(`${ok ? 'ok    ' : 'WRONG '} ${text}`);
  if (!ok) failures.push(text);
};
const docker = (...args) => execFileSync('docker', args, { env, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
const tryDocker = (...args) => { try { return docker(...args); } catch { return ''; } };
const onIt = (name, script) => docker('exec', name, 'sh', '-c', script);
const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

const login = await fetch(`${api}/api/auth/admin/login`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: 'dev-superadmin@neoprint.local', password }),
});
if (!login.ok) throw new Error(`sign-in: ${login.status}`);
const token = (await login.json()).accessToken;
const call = async (method, path, body) => {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'Idempotency-Key': crypto.randomUUID() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${(await response.text()).slice(0, 200)}`);
  return response.status === 204 ? null : response.json();
};

const secrets = mkdtempSync(join(tmpdir(), 'neoprint-golden-'));
const tokenFile = join(secrets, 'token');
const removeMachines = () => {
  for (const name of [master, ...clones]) tryDocker('rm', '-f', name);
  for (const name of [`${master}-data`, ...clones.map((clone) => `${clone}-data`)]) tryDocker('volume', 'rm', '-f', name);
  tryDocker('rmi', '-f', image);
};
const cleanup = () => {
  removeMachines();
  rmSync(secrets, { recursive: true, force: true });
};

try {
  removeMachines();

  // ---- The token: from the panel, or made here for two new kiosks ----
  const given = process.argv.indexOf('--token-file');
  let tokenId = null;
  if (given > 0) {
    writeFileSync(tokenFile, readFileSync(process.argv[given + 1], 'utf8').trim());
  }
  else {
    const made = await call('POST', '/api/admin/enrollment-tokens', {
      newKiosk: {
        name: 'Golden image clone', lat: 40.4093, lng: 49.8671, address: 'Bakı (golden image sınağı)',
        defaultLanguage: 'az', paperCapacityA4: 500, paperCapacityA3: 0, adminPin: '135790',
      },
      ttlHours: 1,
      maxUses: 2,
    });
    tokenId = made.id;
    writeFileSync(tokenFile, made.token);
  }
  chmodSync(tokenFile, 0o600);

  // ---- 1. The master ----
  console.log('== the master: built, started with no token, registered');
  execFileSync('bash', [join(root, 'tools/dev-kiosk/build-linux-kiosk.sh')], {
    // Without MSYS_NO_PATHCONV: the build script turns its own paths for Docker.
    env: { ...process.env, CONTAINER: master, VOLUME: `${master}-data`, VERSION: '0.9.0-m19a' },
    stdio: ['ignore', 'ignore', 'inherit'],
  });
  await sleep(10_000);
  const masterKey = onIt(master, 'sha256sum /var/lib/neoprint/device/key.pem 2>/dev/null | cut -c1-16');
  check(masterKey.length > 0, 'the master made a device key, as a machine does before it is captured');

  onIt(master, '/opt/neoprint/provisioning/current/prepare-image.sh >/dev/null');
  const verified = onIt(master, '/opt/neoprint/provisioning/current/prepare-image.sh --check 2>&1 | grep clean: || true');
  check(verified.includes('clean'), `prepare-image.sh --check: ${verified}`);
  check(onIt(master, 'test ! -e /var/lib/neoprint/device/key.pem && echo gone') === 'gone', 'no device key in the image');

  // ---- 2. The image ----
  console.log('== the image: written onto two machines');
  docker('stop', master);
  docker('commit', master, image);
  for (const clone of clones) {
    docker('volume', 'create', `${clone}-data`);
    docker('run', '--rm', '-v', `${master}-data:/from:ro`, '-v', `${clone}-data:/to`, 'alpine', 'sh', '-c', 'cp -a /from/. /to/');
  }

  // ---- 3. The clones ----
  console.log('== the clones: first boot, the token, activated');
  for (const clone of clones) {
    docker('run', '-d', '--name', clone, '--network', 'neoprint_default', '--tmpfs', '/media/kiosk/NEOSTICK:size=20m',
      '-v', `${clone}-data:/var/lib/neoprint`, '--entrypoint', 'sh', image, '-c',
      '/opt/neoprint/provisioning/current/neoprint-firstboot; exec /run.sh --api-url http://neoprint-api:8080');
  }
  await sleep(12_000);
  for (const clone of clones) {
    docker('cp', tokenFile, `${clone}:/tmp/enrollment-token`);
    onIt(clone, '/opt/neoprint/provisioning/current/neoprint-enroll --from-file /tmp/enrollment-token >/dev/null; rm -f /tmp/enrollment-token');
    docker('restart', clone);
  }

  // ---- 4. What came of it ----
  const hostnames = clones.map((clone) => docker('inspect', '-f', '{{.Config.Hostname}}', clone));
  let active = [];
  for (let i = 0; i < 30 && active.length < 2; i++) {
    await sleep(3_000);
    active = (await call('GET', '/api/admin/devices?status=Active')).filter((device) => hostnames.includes(device.hostname));
  }
  check(active.length === 2, `both clones activated themselves (${active.length} of 2)`);
  check(new Set(active.map((device) => device.id)).size === 2, 'as two devices');
  check(new Set(active.map((device) => device.kioskCode)).size === 2 && active.every((device) => device.kioskCode), `as two kiosks: ${active.map((device) => device.kioskCode).join(', ')}`);

  const keys = clones.map((clone) => onIt(clone, 'sha256sum /var/lib/neoprint/device/key.pem | cut -c1-16'));
  check(keys[0] && keys[1] && keys[0] !== keys[1] && !keys.includes(masterKey), 'each clone made a device key of its own, neither the master\'s');
  const machineIds = clones.map((clone) => onIt(clone, 'cat /etc/machine-id'));
  check(machineIds[0] && machineIds[1] && machineIds[0] !== machineIds[1], 'and a machine id of its own (neoprint-firstboot)');
  check(clones.every((clone) => onIt(clone, 'test -e /var/lib/neoprint/device/enrollment-token && echo left || echo gone') === 'gone'), 'the token file is gone once each is activated');

  if (tokenId) {
    const row = (await call('GET', '/api/admin/enrollment-tokens')).find((item) => item.id === tokenId);
    check(row?.uses === 2 && row?.maxUses === 2 && row?.isUsable === false, `the token is used ${row?.uses} of ${row?.maxUses}, and no longer usable`);
  }

  // Their kiosks stay, NotActivated; the devices go.
  for (const device of active) {
    await call('POST', `/api/admin/devices/${device.id}/revoke`, { reason: 'golden image sınağı bitdi (T-327)' });
  }
}
finally {
  cleanup();
}

console.log(failures.length ? `\n${failures.length} check(s) failed.` : '\nEvery check passed.');
process.exit(failures.length ? 1 : 0);
