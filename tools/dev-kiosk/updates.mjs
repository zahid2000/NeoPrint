// ---------------------------------------------------------------------------
// Releases and updates on the development stack as the development SuperAdmin
// (docs/KIOSK_UPDATES.md, T-321) - what the panel's "Buraxılışlar" page does,
// and what a kiosk then reports.
//
//   node tools/dev-kiosk/updates.mjs upload <folder> [Pilot|Stable]          # a make-release.sh folder; prints the release id
//   node tools/dev-kiosk/updates.mjs rollout <releaseId> <kioskCode> [urgent] # to that kiosk alone
//   node tools/dev-kiosk/updates.mjs status <kioskCode>                       # versions and the last update report
//   node tools/dev-kiosk/updates.mjs wait <kioskCode> <component> <version> <status> [minutes]
//   node tools/dev-kiosk/updates.mjs incidents <kioskCode>                    # its open UpdateFailed incidents
//   node tools/dev-kiosk/updates.mjs logs <kioskCode>                         # its uploaded logs; the newest, unpacked
//   node tools/dev-kiosk/updates.mjs withdraw <releaseId> <reason>
//
// Needs the compose stack up and the development accounts of
// docs/DEV_ENVIRONMENT.md section 4. No dependencies: Node 20 or later.
// ---------------------------------------------------------------------------
import { randomUUID } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { basename, join } from 'node:path';
import { gunzipSync } from 'node:zlib';

const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
const email = process.env.NEOPRINT_DEV_SUPERADMIN_EMAIL ?? 'dev-superadmin@neoprint.local';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const [command, ...args] = process.argv.slice(2);

const login = await fetch(`${api}/api/auth/admin/login`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email, password }),
});
if (!login.ok) {
  console.error(`Could not sign in: ${login.status}. Is the stack up (docker compose up -d)?`);
  process.exit(2);
}

const headers = { Authorization: `Bearer ${(await login.json()).accessToken}`, 'Accept-Language': 'az' };
const call = async (method, path, body) => {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { ...headers, 'Idempotency-Key': randomUUID(), ...(body ? { 'Content-Type': 'application/json' } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await response.text();
  if (!response.ok) {
    console.error(`${method} ${path}: ${response.status} ${text.slice(0, 400)}`);
    process.exit(1);
  }
  return text ? JSON.parse(text) : null;
};
const kioskOf = async (code) => {
  const kiosk = (await call('GET', '/api/admin/kiosks')).find((item) => item.code === code);
  if (!kiosk) {
    console.error(`No kiosk with the code ${code}.`);
    process.exit(1);
  }
  return kiosk;
};
const versionOf = async (kioskId) => (await call('GET', '/api/admin/kiosks/versions')).find((row) => row.kioskId === kioskId);
const show = (row) => {
  console.log(`${row.code}: app ${row.appVersion ?? '-'}, agent ${row.agentVersion ?? '-'}, provisioning ${row.provisioningVersion ?? '-'}, offered ${row.offered.map((o) => `${o.component} ${o.version}`).join(', ') || '-'}`);
  for (const update of row.updates) {
    console.log(`  ${update.component} ${update.version}: ${update.status}${update.message ? ` - ${update.message}` : ''} (${update.reportedAt})`);
  }
};

switch (command) {
  case 'upload': {
    const folder = args[0];
    const file = readdirSync(folder).find((name) => name.endsWith('.tar.gz') || name.endsWith('.zip'));
    const form = new FormData();
    form.append('package', new Blob([readFileSync(join(folder, file))]), file);
    form.append('manifest', new Blob([readFileSync(join(folder, `${file}.manifest.json`))]), `${file}.manifest.json`);
    form.append('signature', new Blob([readFileSync(join(folder, `${file}.manifest.json.sig`))]), `${file}.manifest.json.sig`);
    form.append('channel', args[1] ?? 'Pilot');
    const response = await fetch(`${api}/api/admin/releases`, { method: 'POST', headers: { ...headers, 'Idempotency-Key': randomUUID() }, body: form });
    const body = await response.json();
    if (!response.ok) {
      console.error(`Upload of ${basename(file)} refused: ${response.status} ${JSON.stringify(body.errors ?? body.detail ?? body)}`);
      process.exit(1);
    }
    console.log(`${body.component} ${body.version} (${body.keySet}, key ${body.keyId}): release ${body.id}`);
    break;
  }

  case 'rollout': {
    const kiosk = await kioskOf(args[1]);
    const rollout = await call('POST', '/api/admin/rollouts', { releaseId: args[0], target: 'Kiosks', kioskIds: [kiosk.id], urgent: args[2] === 'urgent' });
    console.log(`Rollout ${rollout.id}: ${rollout.component} ${rollout.version} to ${args[1]}${rollout.urgent ? ' (urgent)' : ''}`);
    break;
  }

  case 'status':
    show(await versionOf((await kioskOf(args[0])).id));
    break;

  case 'wait': {
    const [code, component, version, status, minutes = '15'] = args;
    const kiosk = await kioskOf(code);
    const until = Date.now() + Number(minutes) * 60_000;
    let last = '';
    while (Date.now() < until) {
      const row = await versionOf(kiosk.id);
      const update = row.updates.find((item) => item.component === component);
      const now = update ? `${update.version} ${update.status}${update.message ? ` - ${update.message}` : ''}` : 'no report';
      if (now !== last) {
        console.log(`${new Date().toISOString()}  ${component} ${now}`);
        last = now;
      }
      if (update?.version === version && update.status === status) {
        show(row);
        process.exit(0);
      }
      await new Promise((resolve) => setTimeout(resolve, 3000));
    }
    console.error(`${component} ${version} did not reach ${status} in ${minutes} minutes.`);
    process.exit(1);
  }

  case 'incidents': {
    const kiosk = await kioskOf(args[0]);
    const page = await call('GET', `/api/admin/incidents?kioskId=${kiosk.id}&type=UpdateFailed&pageSize=20`);
    for (const incident of page.items ?? page) {
      console.log(`${incident.createdAt}  ${incident.type} ${incident.severity} ${incident.status}: ${incident.details}`);
    }
    break;
  }

  case 'logs': {
    const kiosk = await kioskOf(args[0]);
    const logs = await call('GET', `/api/admin/kiosks/${kiosk.id}/logs`);
    for (const log of logs) {
      console.log(`${log.createdAt}  ${log.id}  ${log.lines} lines, ${log.size} bytes, ${log.redacted} taken out by the backend`);
    }
    if (logs[0]) {
      const file = await fetch(`${api}/api/admin/kiosks/${kiosk.id}/logs/${logs[0].id}`, { headers });
      const text = gunzipSync(Buffer.from(await file.arrayBuffer())).toString('utf8');
      console.log(`--- newest, ${text.split('\n').length} lines; first and last:`);
      console.log(text.split('\n').slice(0, 3).join('\n'));
      console.log(text.split('\n').slice(-4).join('\n'));
    }
    break;
  }

  case 'withdraw':
    await call('POST', `/api/admin/releases/${args[0]}/withdraw`, { reason: args.slice(1).join(' ') || 'withdrawn in a dev check' });
    console.log(`Release ${args[0]} withdrawn.`);
    break;

  default:
    console.error('upload | rollout | status | wait | incidents | logs | withdraw - see the top of this file.');
    process.exit(2);
}
