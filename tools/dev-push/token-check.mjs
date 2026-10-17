#!/usr/bin/env node
// A push to a device token FCM does not know (T-283): the token is dropped from
// the account, the notification is finished, and nothing reaches the
// notification-delivery_error queue.
//
// Runs on the local stack with a throwaway account (registered here, removed at
// the end); the seeded dev accounts are not touched. With the FCM key in
// OpenBao the token goes to Google, which refuses it; without it the API only
// logs the push (LoggingPushSender) and the script says so.
//
//   node tools/dev-push/token-check.mjs
//
// NEOPRINT_API (default http://localhost:5080), NEOPRINT_MAILPIT (http://localhost:8025),
// NEOPRINT_RABBIT_MANAGEMENT (http://localhost:15672).

import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';

const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
const mailpit = process.env.NEOPRINT_MAILPIT ?? 'http://localhost:8025';
const rabbit = process.env.NEOPRINT_RABBIT_MANAGEMENT ?? 'http://localhost:15672';
const queue = 'notification-delivery_error';
const suffix = randomUUID().slice(0, 8);
const email = `t283-${suffix}@neoprint.test`;
const password = `Push!${randomUUID()}`;
const deviceToken = `t283-not-a-device-token-${randomUUID()}`;

let failed = 0;
const check = (ok, label) => {
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${label}`);
  if (!ok) failed++;
};

const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function json(method, path, body, token) {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { 'Content-Type': 'application/json', 'Accept-Language': 'az', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await response.text();
  return { status: response.status, body: text ? JSON.parse(text) : null };
}

function mongo(script) {
  return execFileSync(
    'docker',
    ['exec', 'neoprint-mongo', 'mongosh', '-u', process.env.NEOPRINT_MONGO_USER ?? 'neoprint', '-p', process.env.NEOPRINT_MONGO_PASSWORD ?? 'devMongoPassword',
      '--authenticationDatabase', 'admin', 'neoprint', '--quiet', '--eval', script],
    { encoding: 'utf8' },
  ).trim();
}

function rabbitAuth() {
  const env = (name) => execFileSync('docker', ['exec', 'neoprint-rabbitmq', 'printenv', name], { encoding: 'utf8' }).trim();
  return `Basic ${Buffer.from(`${env('RABBITMQ_DEFAULT_USER')}:${env('RABBITMQ_DEFAULT_PASS')}`).toString('base64')}`;
}

async function deadLetters() {
  const response = await fetch(`${rabbit}/api/queues/%2F/${queue}?columns=messages`, { headers: { authorization: rabbitAuth() } });
  return response.ok ? (await response.json()).messages ?? 0 : 0;
}

async function confirmationCode() {
  for (let attempt = 0; attempt < 30; attempt++) {
    const found = await (await fetch(`${mailpit}/api/v1/search?query=${encodeURIComponent(`to:${email}`)}&limit=1`)).json();
    if (found.messages?.length) {
      const message = await (await fetch(`${mailpit}/api/v1/message/${found.messages[0].ID}`)).json();
      const code = /\b(\d{6})\b/.exec(message.Text ?? '')?.[1];
      if (code) return code;
    }
    await wait(1000);
  }
  throw new Error('no confirmation e-mail in Mailpit');
}

const since = new Date().toISOString();
let userId;

try {
  const privacy = await json('GET', '/api/legal/Privacy?lang=az');
  const terms = await json('GET', '/api/legal/Terms?lang=az');
  const registered = await json('POST', '/api/auth/register', {
    email,
    password,
    language: 'az',
    consents: [
      { docType: 'Privacy', version: privacy.body.version },
      { docType: 'Terms', version: terms.body.version },
    ],
  });
  if (registered.status !== 200) throw new Error(`register: ${registered.status}`);

  const confirmed = await json('POST', '/api/auth/confirm-email', { email, code: await confirmationCode() });
  if (confirmed.status !== 200) throw new Error(`confirm: ${confirmed.status}`);
  const token = confirmed.body.accessToken;
  userId = JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()).sub;

  const device = await json('POST', '/api/users/me/fcm-token', { token: deviceToken, language: 'az', platform: 'android' }, token);
  check(device.status === 204, 'a device token that is not one is registered');

  const before = await deadLetters();
  const exported = await json('POST', '/api/users/me/export', null, token);
  check(exported.status === 202 || exported.status === 200, `the data export is asked for (${exported.status})`);

  // The export ends in an ExportReady notification, delivered to the one device.
  let state;
  for (let attempt = 0; attempt < 60; attempt++) {
    state = JSON.parse(mongo(`
      const n = db.notifications.findOne({ UserId: UUID('${userId}'), Type: 'ExportReady' });
      const u = db.users.findOne({ _id: UUID('${userId}') });
      print(JSON.stringify({ push: n ? n.Push : null, devices: u ? (u.FcmTokens || []).length : -1 }));`));
    if (state.push && state.push !== 'Pending') break;
    await wait(1000);
  }

  const logs = execFileSync('docker', ['logs', '--since', since, 'neoprint-api'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  const realFcm = /FCM refused token/.test(logs);
  const loggingOnly = /Push not sent \(FCM not configured\)/.test(logs);
  console.log(`     sender: ${realFcm ? 'FCM HTTP v1 (Google refused the token)' : loggingOnly ? 'LoggingPushSender (no FCM key in OpenBao)' : 'unknown'}`);

  check(state.push && state.push !== 'Pending', `the notification is finished (push ${state.push})`);
  await wait(6000); // the broker's queue counters lag a few seconds
  check((await deadLetters()) === before, `nothing new in ${queue} (${before})`);
  if (realFcm) {
    check(state.devices === 0, 'the token FCM refused is dropped from the account');
    check(/Push token .* is no longer valid; removed/.test(logs), 'the log says so, by the token hash only');
    check(!logs.includes(deviceToken), 'the token itself is not in the log');
  } else {
    console.log('     (without FCM the token is not checked by Google: only the delivery path is shown)');
  }
} finally {
  if (userId) {
    mongo(`
      const id = UUID('${userId}');
      db.notifications.deleteMany({ UserId: id });
      db.data_exports.deleteMany({ UserId: id });
      db.refresh_tokens.deleteMany({ UserId: id });
      db.consents.deleteMany({ UserId: id });
      db.users.deleteOne({ _id: id });`);
  }
}

console.log(failed ? `\n${failed} check(s) failed` : '\nall checks passed');
process.exit(failed ? 1 : 0);
