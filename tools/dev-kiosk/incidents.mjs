#!/usr/bin/env node
// Shows a kiosk's status and its incidents as the development admin, and
// acknowledges or resolves one (plan section 9.6) - what the panel's
// "Incident-lər" page will do.
//
//   node tools/dev-kiosk/incidents.mjs                      # KSK-9002: status, open incidents
//   node tools/dev-kiosk/incidents.mjs list KSK-0001        # another kiosk, by its code
//   node tools/dev-kiosk/incidents.mjs all                  # resolved ones too
//   node tools/dev-kiosk/incidents.mjs show <incidentId>    # with the orders, payments and refunds it touched
//   node tools/dev-kiosk/incidents.mjs ack <incidentId>
//   node tools/dev-kiosk/incidents.mjs resolve <incidentId> "what was done"
//
// To stop the fake printer of the Linux container kiosk (docs/DEV_ENVIRONMENT.md):
//   docker exec neoprint-kshot sh -c 'echo OutOfPaper > /var/lib/neoprint/spool/printed/printer-state.txt'
//   docker exec neoprint-kshot rm /var/lib/neoprint/spool/printed/printer-state.txt     # it is back
//
// Needs the compose stack up and the development admin of
// docs/DEV_ENVIRONMENT.md section 4. No dependencies: Node 20 or later.

const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
const email = process.env.NEOPRINT_DEV_ADMIN_EMAIL ?? 'dev-admin@neoprint.local';
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const command = process.argv[2] ?? 'list';
const known = ['list', 'all', 'show', 'ack', 'resolve'];

if (!known.includes(command)) {
  console.error(`Unknown command "${command}". One of: ${known.join(', ')}.`);
  process.exit(1);
}

const login = await fetch(`${api}/api/auth/admin/login`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email, password }),
});

if (!login.ok) {
  console.error(`Could not sign in as ${email}: ${login.status}. Is the stack up (docker compose up -d)?`);
  process.exit(2);
}

const headers = {
  Authorization: `Bearer ${(await login.json()).accessToken}`,
  'Accept-Language': 'az',
  'Content-Type': 'application/json',
};

async function call(method, path, body) {
  const response = await fetch(`${api}${path}`, { method, headers, body: body ? JSON.stringify(body) : undefined });
  const answer = await response.json().catch(() => ({}));

  if (!response.ok) {
    console.error(`${method} ${path} was refused: ${response.status} ${answer.detail ?? answer.title ?? ''}`);
    process.exit(1);
  }

  return answer;
}

const time = (value) => (value ? new Date(value).toISOString().slice(11, 19) : '-');
const line = (incident) =>
  `  ${time(incident.createdAt)}  ${incident.severity.padEnd(8)} ${incident.type.padEnd(22)} ${incident.status.padEnd(12)} ${incident.id}\n` +
  `            ${incident.details}` +
  (incident.resolution ? `\n            → ${incident.resolution}` : '');

if (command === 'list' || command === 'all') {
  const code = process.argv[3] ?? 'KSK-9002';
  const kiosk = (await call('GET', '/api/admin/kiosks')).find((item) => item.code === code);

  if (!kiosk) {
    console.error(`No kiosk with the code ${code}.`);
    process.exit(1);
  }

  const status = command === 'all' ? '' : '&status=Open&status=Acknowledged';
  const page = await call('GET', `/api/admin/incidents?kioskId=${kiosk.id}&pageSize=50${status}`);

  console.log(`${code} is ${kiosk.status}; ${page.total} incident(s)${command === 'all' ? '' : ' not resolved'}`);
  page.items.forEach((incident) => console.log(line(incident)));
} else {
  const id = process.argv[3];

  if (!id) {
    console.error(`${command} needs an incident id (see "list").`);
    process.exit(1);
  }

  if (command === 'ack') {
    console.log(line(await call('POST', `/api/admin/incidents/${id}/acknowledge`)));
  } else if (command === 'resolve') {
    console.log(line(await call('POST', `/api/admin/incidents/${id}/resolve`, { note: process.argv[4] ?? '' })));
  } else {
    const detail = await call('GET', `/api/admin/incidents/${id}`);

    console.log(line(detail.incident));
    console.log(`  ${detail.affected.length} order(s) touched`);

    for (const order of detail.affected) {
      const paid = order.paid === null ? 'not paid' : `${order.paid.toFixed(2)} ${order.currency} (${order.paymentStatus})`;
      console.log(`    ${order.jobCode}  ${order.status.padEnd(12)} ${order.registered ? 'registered' : 'guest     '}  ${paid}`);

      for (const refund of order.refunds) {
        console.log(`        refund ${refund.amount.toFixed(2)} → ${refund.destination}: ${refund.status} (${refund.reason})`);
      }
    }
  }
}
