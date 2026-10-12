#!/usr/bin/env node
// Places an order at a kiosk as the development user - what the app does in
// a kiosk session (plan section 9.3) - pays it from the wallet, and shows
// what becomes of it. For walking plan section 9.6 by hand: a registered
// user's order at a kiosk whose printer stops.
//
//   node tools/dev-kiosk/app-order.mjs                     # order the first ready file at KSK-9002 and pay
//   node tools/dev-kiosk/app-order.mjs order KSK-0001      # at another kiosk
//   node tools/dev-kiosk/app-order.mjs list                # my orders: status, what may be cancelled, a stopped kiosk
//   node tools/dev-kiosk/app-order.mjs cancel <jobId>      # "Ləğv et və məbləği qaytar"
//   node tools/dev-kiosk/app-order.mjs notifications       # the last notifications (the refund offer)
//
// The user needs one ready file (upload one in the app first) - the script
// tops the wallet up with the mock card when it is short.
// Needs the compose stack up and the development user of
// docs/DEV_ENVIRONMENT.md section 4. No dependencies: Node 20 or later.

import { randomUUID } from 'node:crypto';

const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
const email = process.env.NEOPRINT_DEV_USER_EMAIL ?? 'dev-user@neoprint.local';
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const command = process.argv[2] ?? 'order';
const known = ['order', 'list', 'cancel', 'notifications'];

if (!known.includes(command)) {
  console.error(`Unknown command "${command}". One of: ${known.join(', ')}.`);
  process.exit(1);
}

const login = await fetch(`${api}/api/auth/login`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email, password }),
});

if (!login.ok) {
  console.error(
    login.status === 429
      ? `Too many sign-ins in a minute (the API allows 10): wait a minute and run it again.`
      : `Could not sign in as ${email}: ${login.status}. Is the stack up (docker compose up -d)?`,
  );
  process.exit(2);
}

const headers = {
  Authorization: `Bearer ${(await login.json()).accessToken}`,
  'Accept-Language': 'az',
  'Content-Type': 'application/json',
};

async function call(method, path, body, { idempotent = false, allow = [] } = {}) {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: idempotent ? { ...headers, 'Idempotency-Key': randomUUID() } : headers,
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await response.text();
  const answer = text ? JSON.parse(text) : {};

  if (!response.ok && !allow.includes(response.status)) {
    console.error(`${method} ${path} was refused: ${response.status} ${answer.detail ?? answer.title ?? ''}`);

    for (const [field, messages] of Object.entries(answer.errors ?? {})) {
      console.error(`  ${field}: ${[].concat(messages).join(' ')}`);
    }

    process.exit(1);
  }

  return answer;
}

const time = (value) => (value ? new Date(value).toISOString().slice(11, 19) : '-');

async function list() {
  for (const job of (await call('GET', '/api/print-jobs/my')).slice(0, 8)) {
    console.log(
      `  ${time(job.createdAt)}  ${job.jobCode}  ${job.status.padEnd(12)} ${String(job.kioskCode).padEnd(9)} ` +
        `${job.amount.toFixed(2)} ${job.currency}  ${job.canCancel ? 'can cancel' : '          '} ` +
        `${job.kioskStopped ? 'KIOSK STOPPED' : ''}  ${job.id}` +
        (job.reason ? `\n            ${job.reason}` : ''),
    );
  }
}

if (command === 'list') {
  await list();
} else if (command === 'notifications') {
  const page = await call('GET', '/api/notifications');

  for (const item of page.items.slice(0, 6)) {
    console.log(`  ${time(item.createdAt)}  ${item.type}\n            ${item.title} — ${item.body}`);
  }
} else if (command === 'cancel') {
  const id = process.argv[3];

  if (!id) {
    console.error('cancel needs a job id (see "list").');
    process.exit(1);
  }

  await call('POST', `/api/print-jobs/my/${id}/cancel`);
  console.log('Cancelled; what was paid goes back.');
  await new Promise((resolve) => setTimeout(resolve, 3000));
  await list();
  console.log(`  wallet: ${(await call('GET', '/api/wallet')).balance.toFixed(2)}`);
} else {
  const kiosk = process.argv[3] ?? 'KSK-9002';
  const files = await call('GET', '/api/files');
  const file = (files.items ?? files).find((item) => item.status === 'Ready');

  if (!file) {
    console.error(`${email} has no ready file. Upload one in the app first (Fayllarım → Yüklə).`);
    process.exit(1);
  }

  const session = await call('POST', '/api/sessions', { kiosk });
  const options = (scaling) => ({
    copies: 1,
    pageRange: null,
    color: false,
    duplex: false,
    paper: 'A4',
    orientation: 'Auto',
    pagesPerSheet: 1,
    scaling,
  });

  // One black and white copy, as the app proposes. A scan or a layout is
  // printed at its real size only, so that is asked for when the first try
  // is refused for its scaling.
  let order = await call('POST', `/api/sessions/${session.id}/print-jobs`, { fileId: file.id, options: options('Fit') }, {
    idempotent: true,
    allow: [400],
  });

  if (order.errors?.scaling) {
    order = await call('POST', `/api/sessions/${session.id}/print-jobs`, { fileId: file.id, options: options('Actual') }, {
      idempotent: true,
    });
  } else if (order.errors) {
    console.error(`The order was refused: ${JSON.stringify(order.errors)}`);
    process.exit(1);
  }

  if ((await call('GET', '/api/wallet')).balance < order.amount) {
    await call('POST', '/api/wallet/top-up', { amount: 5 }, { idempotent: true });
  }

  await call('POST', `/api/payments/${order.paymentId}/pay-with-app`, { source: 'Wallet', useBonus: false }, { idempotent: true });

  console.log(`${kiosk}: order ${order.jobCode} (${file.fileName ?? file.name}), ${order.amount.toFixed(2)} ${order.currency} paid from the wallet`);
  console.log(`  job ${order.jobId}`);
  await new Promise((resolve) => setTimeout(resolve, 5000));
  await list();
}
