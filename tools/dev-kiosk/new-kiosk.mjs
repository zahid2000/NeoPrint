#!/usr/bin/env node
// A kiosk that has just been installed, without the hardware: a device
// registers with a key made here, the development SuperAdmin activates it as a
// new kiosk, and nothing else is done to it - its paper counter is the one
// nobody has set yet (T-258). For looking at what a pilot installation looks
// like in the panel before the first refill is recorded.
//
//   node tools/dev-kiosk/new-kiosk.mjs                  # makes one and shows its supplies
//   node tools/dev-kiosk/new-kiosk.mjs show <kioskId>   # its supplies, alerts and tasks now
//   node tools/dev-kiosk/new-kiosk.mjs refill <kioskId> <sheets>   # the first refill ("Exact")
//   node tools/dev-kiosk/new-kiosk.mjs retire <kioskId> # revokes the device: the kiosk is NotActivated again
//
// The device is nobody's: its private key is thrown away, so it never calls in
// and the kiosk goes Offline after five minutes. Retire it when done: a kiosk that
// has had a device keeps its history and cannot be deleted (kiosks.deleteHasHistory),
// so it stays in the list as NotActivated; its alert and its task end with the next
// sweep of the supply evaluator.
// Needs the compose stack up and the development accounts of
// docs/DEV_ENVIRONMENT.md section 4. No dependencies: Node 20 or later.

import { generateKeyPairSync, randomUUID } from 'node:crypto';

const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
const email = process.env.NEOPRINT_DEV_SUPERADMIN_EMAIL ?? 'dev-superadmin@neoprint.local';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const [command = 'new', kioskId, amount] = process.argv.slice(2);

if (!['new', 'show', 'refill', 'retire'].includes(command) || (command !== 'new' && !kioskId)) {
  console.error('Usage: new-kiosk.mjs [new | show <kioskId> | refill <kioskId> <sheets> | retire <kioskId>]');
  process.exit(1);
}

const login = await fetch(`${api}/api/auth/admin/login`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email, password }),
});

if (!login.ok) {
  console.error(
    login.status === 429
      ? 'Too many sign-ins in a minute (the API allows 10): wait a minute and run it again.'
      : `Could not sign in as ${email}: ${login.status}. Is the stack up (docker compose up -d)?`,
  );
  process.exit(2);
}

const token = (await login.json()).accessToken;

async function call(method, path, body, anonymous = false) {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: {
      ...(anonymous ? {} : { Authorization: `Bearer ${token}` }),
      'Content-Type': 'application/json',
      'Idempotency-Key': randomUUID(),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  if (!response.ok) {
    throw new Error(`${method} ${path}: ${response.status} ${(await response.text()).slice(0, 300)}`);
  }

  return response.status === 204 ? null : response.json();
}

async function show(id) {
  const kiosk = await call('GET', `/api/admin/kiosks/${id}`);
  const supplies = await call('GET', `/api/admin/kiosks/${id}/supplies`);
  const tasks = await call('GET', `/api/admin/service-tasks?kioskId=${id}&pageSize=20`);

  console.log(`${kiosk.code} — ${kiosk.name}: ${kiosk.status}`);

  for (const [size, tray] of Object.entries(supplies.paper)) {
    const counter = tray.capacity === 0 ? 'no tray' : tray.counted === false ? 'counter NOT SET' : `${tray.current} / ${tray.capacity} sheets`;
    console.log(`  ${size}: ${counter}${tray.lastRefillAt ? `, last refill ${tray.lastRefillAt}` : ''}`);
  }

  console.log(`  forecasts: ${supplies.forecasts?.length ?? 0}; alerts: ${(supplies.alerts ?? []).map((alert) => `${alert.level} ${alert.resource} ${alert.value}`).join(', ') || 'none'}`);
  console.log(`  service tasks: ${tasks.items.map((task) => `${task.status} ${task.priority} ${task.type}${task.assignedToName ? ` → ${task.assignedToName}` : ' → nobody'}`).join('; ') || 'none'}`);
}

try {
  if (command === 'new') {
    const { publicKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
    const stamp = new Date().toISOString().replace(/\D/g, '').slice(4, 14);

    const registered = await call(
      'POST',
      '/api/devices/register',
      {
        publicKey: publicKey.export({ type: 'spki', format: 'der' }).toString('base64'),
        fingerprint: {
          hostname: `dev-new-${stamp}`,
          os: 'Linux',
          osVersion: '6.1',
          machineId: randomUUID().replaceAll('-', ''),
          macAddresses: ['02:00:00:00:00:01'],
          cpu: 'Development',
          ramGb: 8,
          screenResolution: '1920x1080',
        },
        platform: 'Linux',
        appVersion: '0.7.0',
        agentVersion: '0.7.0',
        printers: [{ name: 'Test printer', driverName: 'Test driver', isOnline: true, color: true, duplex: true, a3: false }],
      },
      true,
    );

    const activated = await call('POST', `/api/admin/devices/${registered.deviceId}/activate`, {
      newKiosk: {
        name: `Yeni quraşdırılmış ${stamp}`,
        lat: 40.3793,
        lng: 49.8486,
        address: 'Bakı (dev)',
        defaultLanguage: 'az',
        paperCapacityA4: 500,
        paperCapacityA3: 0,
        adminPin: String(100000 + Math.floor(Math.random() * 899999)),
      },
      selectedPrinterName: 'Test printer',
    });

    console.log(`Made ${activated.kioskCode}. Its id, for the other commands: ${activated.kioskId}`);
    await show(activated.kioskId);
  } else if (command === 'show') {
    await show(kioskId);
  } else if (command === 'refill') {
    await call('POST', `/api/admin/kiosks/${kioskId}/paper/refill`, { paper: 'A4', mode: 'Exact', sheets: Number(amount ?? 0) });
    await show(kioskId);
  } else {
    const kiosk = await call('GET', `/api/admin/kiosks/${kioskId}`);

    if (kiosk.activeDeviceId) {
      await call('POST', `/api/admin/devices/${kiosk.activeDeviceId}/revoke`, { reason: 'Development kiosk retired' });
      console.log(`${kiosk.code}: its device is revoked; the kiosk is NotActivated and keeps its history.`);
    } else {
      console.log(`${kiosk.code} has no device.`);
    }
  }
} catch (error) {
  console.error(String(error.message ?? error));
  process.exit(1);
}
