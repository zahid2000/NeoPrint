// ---------------------------------------------------------------------------
// What a check on the development stack brings with it, and how it takes it
// away again (T-266): a kiosk of its own with a machine that is nobody's, and
// the removal of everything that was written for them.
//
// The API deletes none of this - a kiosk that has had a device keeps its
// history, a task is cancelled and an item switched off (docs/DECISIONS.md) -
// so the removal goes to the development database itself, through the
// container the compose file starts. It only ever names ids the check made a
// moment ago. The audit log is left alone: it is never rewritten, not even here.
//
// Not for anything but the development stack: it refuses an API that is not on
// this machine.
// ---------------------------------------------------------------------------
import { execFileSync } from 'node:child_process';
import { generateKeyPairSync, randomUUID, sign } from 'node:crypto';

const container = process.env.NEOPRINT_MONGO_CONTAINER ?? 'neoprint-mongo';
// The compose file's development database account (docs/DEV_ENVIRONMENT.md section 3); never a real one.
const mongoUser = process.env.NEOPRINT_MONGO_USER ?? 'neoprint';
const mongoPassword = process.env.NEOPRINT_MONGO_PASSWORD ?? 'devMongoPassword';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function ids(list) {
  for (const id of list) {
    if (!uuid.test(id)) throw new Error(`Not an id: ${id}`);
  }

  return `[${list.map((id) => `UUID("${id}")`).join(',')}]`;
}

/** Runs a script in the development database and gives back what it printed. */
function mongo(script) {
  return execFileSync(
    'docker',
    ['exec', container, 'mongosh', '-u', mongoUser, '-p', mongoPassword, '--authenticationDatabase', 'admin', 'neoprint', '--quiet', '--eval', script],
    { encoding: 'utf8', env: { ...process.env, MSYS_NO_PATHCONV: '1' } },
  ).trim();
}

function localOnly(api) {
  const host = new URL(api).hostname;

  if (host !== 'localhost' && host !== '127.0.0.1') {
    throw new Error(`The check's own data is only ever removed on this machine's stack, not at ${host}.`);
  }
}

/**
 * A machine that has just been switched on: it registers and is given the code
 * its screen would show. Its private key stays in this process - a check that
 * acts as the kiosk signs in with it (kioskSession); nothing writes it down.
 *
 * @param call The check's own `call(method, path, body, anonymous)`.
 * @returns The registration's answer and `privateKey`.
 */
export async function registerMachine(call, hostname) {
  const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });

  const answer = await call(
    'POST',
    '/api/devices/register',
    {
      publicKey: publicKey.export({ type: 'spki', format: 'der' }).toString('base64'),
      fingerprint: {
        hostname,
        os: 'Linux',
        osVersion: '6.1',
        machineId: randomUUID().replaceAll('-', ''),
        macAddresses: ['02:00:00:00:00:02'],
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

  return { ...answer, privateKey };
}

/**
 * Signs in as the kiosk, the way the kiosk application does (plan section
 * 7.11): a challenge, signed with the machine's key.
 *
 * @returns `call(method, path, body, headers)` with the kiosk's token.
 */
export async function kioskSession(api, deviceId, privateKey) {
  const post = async (path, body) => {
    const response = await fetch(`${api}${path}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    if (!response.ok) throw new Error(`${path}: ${response.status}`);
    return response.json();
  };
  const { nonce } = await post('/api/devices/challenge', { deviceId });
  const signature = sign('sha256', Buffer.from(nonce, 'utf8'), { key: privateKey, dsaEncoding: 'ieee-p1363' }).toString('base64');
  const { accessToken } = await post('/api/devices/token', { deviceId, nonce, signature });

  return async (method, path, body, headers = {}) => {
    const response = await fetch(`${api}${path}`, {
      method,
      headers: { Authorization: `Bearer ${accessToken}`, 'Content-Type': 'application/json', 'Idempotency-Key': randomUUID(), ...headers },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) throw new Error(`kiosk ${method} ${path}: ${response.status} ${(await response.text()).slice(0, 200)}`);
    return response.status === 204 || response.status === 202 && response.headers.get('content-length') === '0' ? null : response.json();
  };
}

/**
 * A kiosk for the check alone, activated with a machine of its own.
 *
 * @returns `{ id, code, deviceId }`.
 */
export async function makeKiosk(call, name) {
  const machine = await registerMachine(call, `e2e-${Date.now() % 1_000_000}`);
  const made = await call('POST', `/api/admin/devices/${machine.deviceId}/activate`, {
    newKiosk: {
      name,
      lat: 40.3793,
      lng: 49.8486,
      address: 'Bakı (e2e)',
      defaultLanguage: 'az',
      paperCapacityA4: 500,
      paperCapacityA3: 0,
      adminPin: String(100000 + Math.floor(Math.random() * 899999)),
    },
    selectedPrinterName: 'Test printer',
  });

  return { id: made.kioskId, code: made.kioskCode, deviceId: machine.deviceId, privateKey: machine.privateKey };
}

/**
 * Removes a kiosk the check made, its machines, and everything written about
 * them: tasks, supply movements, alerts, incidents, commands, state changes.
 */
export function purgeKiosk(api, kioskId, deviceIds = []) {
  localOnly(api);

  return mongo(`
    const kiosk = ${ids([kioskId])}[0];
    const devices = ${ids(deviceIds)};
    const own = (name) => db[name].find({ KioskId: kiosk }, { _id: 1 }).toArray().map((row) => row._id);
    const tasks = own('service_tasks');
    const incidents = own('incidents');
    // Orders and their money (T-214): what hangs off a payment carries no kiosk of its own.
    const jobs = own('print_jobs');
    const payments = own('payments');
    const refunds = db.refunds.find({ PaymentId: { $in: payments } }, { _id: 1 }).toArray().map((row) => row._id);
    let removed = 0;

    removed += db.incident_notifications.deleteMany({ IncidentId: { $in: incidents } }).deletedCount;
    removed += db.state_transitions.deleteMany({ EntityId: { $in: [kiosk, ...devices, ...tasks, ...incidents, ...jobs, ...payments, ...refunds] } }).deletedCount;

    for (const name of ['refunds', 'refund_vouchers', 'receipts', 'fiscal_receipts', 'mock_bank_transactions', 'mock_refunds', 'promo_usages']) {
      removed += db[name].deleteMany({ $or: [{ PaymentId: { $in: payments } }, { JobId: { $in: jobs } }] }).deletedCount;
    }

    for (const name of db.getCollectionNames()) {
      // Not the audit log, and nothing that is the database's or the job server's own.
      if (/^(system\\.|hangfire\\.|outbox\\.|inbox\\.|audit_logs$|kiosks$)/.test(name)) continue;

      try {
        removed += db[name].deleteMany({ KioskId: kiosk }).deletedCount;
      } catch {
        // A time series: the check's kiosk never called in, so it has nothing there.
      }
    }

    removed += db.kiosk_devices.deleteMany({ _id: { $in: devices } }).deletedCount;
    removed += db.kiosks.deleteOne({ _id: kiosk }).deletedCount;
    print(removed);
  `);
}

/** Removes a warehouse item the check made, its movements, and the "running low" incidents about it. */
export function purgeItem(api, itemId) {
  localOnly(api);

  return mongo(`
    const item = ${ids([itemId])}[0];
    const incidents = db.incidents.find({ EntityId: item }, { _id: 1 }).toArray().map((row) => row._id);
    let removed = db.inventory_movements.deleteMany({ ItemId: item }).deletedCount + db.inventory_items.deleteOne({ _id: item }).deletedCount;
    removed += db.incident_notifications.deleteMany({ IncidentId: { $in: incidents } }).deletedCount;
    removed += db.state_transitions.deleteMany({ EntityId: { $in: incidents } }).deletedCount;
    removed += db.incidents.deleteMany({ _id: { $in: incidents } }).deletedCount;
    print(removed);
  `);
}

/**
 * An app user for the check alone (T-216): registered, confirmed with the code
 * Mailpit caught, signed in. The seeded accounts are left alone.
 *
 * @returns `{ id, email, password, token }` - the password for signing in on a device, never a real one.
 */
export async function throwawayUser(api, mailpit = process.env.NEOPRINT_MAILPIT ?? 'http://localhost:8025') {
  localOnly(api);

  const email = `e2e-${randomUUID().slice(0, 8)}@neoprint.test`;
  const password = `E2e!${randomUUID().slice(0, 12)}`;
  const json = async (path, body) => {
    const response = await fetch(`${api}${path}`, {
      method: body ? 'POST' : 'GET',
      headers: { 'Content-Type': 'application/json', 'Accept-Language': 'az' },
      body: body ? JSON.stringify(body) : undefined,
    });
    if (!response.ok) throw new Error(`${path}: ${response.status}`);
    return response.json();
  };

  const [privacy, terms] = [await json('/api/legal/Privacy?lang=az'), await json('/api/legal/Terms?lang=az')];
  await json('/api/auth/register', {
    email,
    password,
    language: 'az',
    consents: [{ docType: 'Privacy', version: privacy.version }, { docType: 'Terms', version: terms.version }],
  });

  for (let attempt = 0; attempt < 30; attempt++) {
    const found = await (await fetch(`${mailpit}/api/v1/search?query=${encodeURIComponent(`to:${email}`)}&limit=1`)).json();
    const message = found.messages?.length ? await (await fetch(`${mailpit}/api/v1/message/${found.messages[0].ID}`)).json() : null;
    const code = /\b(\d{6})\b/.exec(message?.Text ?? '')?.[1];

    if (code) {
      const { accessToken } = await json('/api/auth/confirm-email', { email, code });
      const id = JSON.parse(Buffer.from(accessToken.split('.')[1], 'base64url').toString()).sub;
      // A test account from the start: what it orders is not the pilot's (T-300).
      mongo(`print(db.users.updateOne({ _id: ${ids([id])}[0] }, { $set: { IsTestAccount: true } }).modifiedCount);`);
      return { id, email, password, token: accessToken };
    }

    await new Promise((resolve) => setTimeout(resolve, 1000));
  }

  throw new Error('No confirmation e-mail in Mailpit.');
}

/** Removes a user the check made and what was written for them (not the audit log). */
export function purgeUser(api, userId) {
  localOnly(api);

  return mongo(`
    const user = ${ids([userId])}[0];
    let removed = 0;
    for (const name of ['notifications', 'data_exports', 'refresh_tokens', 'consents', 'email_confirmations', 'wallets', 'wallet_transactions']) {
      removed += db[name].deleteMany({ UserId: user }).deletedCount;
    }
    removed += db.users.deleteOne({ _id: user }).deletedCount;
    print(removed);
  `);
}

/** Removes machines the check registered that never became a kiosk's. */
export function purgeDevices(api, deviceIds) {
  localOnly(api);

  return mongo(`print(db.kiosk_devices.deleteMany({ _id: { $in: ${ids(deviceIds)} }, KioskId: null }).deletedCount);`);
}
