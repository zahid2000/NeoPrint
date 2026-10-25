#!/usr/bin/env node
// Sends a remote command to a kiosk as the development admin and shows what
// became of it (plan section 7.8) - what the panel's buttons will do.
//
//   node tools/dev-kiosk/command.mjs pause              # KSK-9002: no new orders, "maintenance" on its screen
//   node tools/dev-kiosk/command.mjs resume
//   node tools/dev-kiosk/command.mjs restart-app        # once the printer is free
//   node tools/dev-kiosk/command.mjs reboot             # restarts the machine; the Linux container kiosk cannot, and says so
//   node tools/dev-kiosk/command.mjs list               # the last commands and their results
//   node tools/dev-kiosk/command.mjs test-print KSK-9007   # a test page; a kiosk with no printer chosen yet goes Online
//   node tools/dev-kiosk/command.mjs update-now KSK-9007   # the agent's commands (T-321): update-now, rollback-version,
//                                                        # upload-logs, reapply-provisioning, restart-agent
//   node tools/dev-kiosk/command.mjs pause KSK-0001     # another kiosk, by its code
//   REASON="why" node tools/dev-kiosk/command.mjs update-now KSK-9007   # the agent's commands need a reason (T-326);
//                                                        # without REASON a dev-check one is sent
//
// Needs the compose stack up and the development admin of
// docs/DEV_ENVIRONMENT.md section 4. No dependencies: Node 20 or later.

const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
const email = process.env.NEOPRINT_DEV_ADMIN_EMAIL ?? 'dev-admin@neoprint.local';
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const command = process.argv[2] ?? 'list';
const code = process.argv[3] ?? 'KSK-9002';
const known = ['pause', 'resume', 'restart-app', 'reboot', 'test-print', 'update-now', 'rollback-version', 'upload-logs', 'reapply-provisioning', 'restart-agent', 'list'];

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

const headers = { Authorization: `Bearer ${(await login.json()).accessToken}`, 'Accept-Language': 'az' };
const kiosk = (await (await fetch(`${api}/api/admin/kiosks`, { headers })).json()).find((item) => item.code === code);

if (!kiosk) {
  console.error(`No kiosk with the code ${code}.`);
  process.exit(1);
}

if (command !== 'list') {
  // The agent's commands are sent with a reason (T-326); the others take none.
  const agent = ['update-now', 'rollback-version', 'upload-logs', 'reapply-provisioning', 'restart-agent'].includes(command);
  const sent = await fetch(`${api}/api/admin/kiosks/${kiosk.id}/${command}`, agent
    ? { method: 'POST', headers: { ...headers, 'Content-Type': 'application/json' }, body: JSON.stringify({ reason: process.env.REASON ?? 'Development check (tools/dev-kiosk)' }) }
    : { method: 'POST', headers });
  const body = await sent.json().catch(() => ({}));

  if (!sent.ok) {
    console.error(`${command} was refused: ${sent.status} ${body.detail ?? body.title ?? ''}`);
    process.exit(1);
  }

  console.log(`${code}: ${body.type} sent (${body.status}), command ${body.id}`);

  // Give the kiosk a moment to answer, so the list below shows it.
  await new Promise((resolve) => setTimeout(resolve, 4000));
}

const now = (await (await fetch(`${api}/api/admin/kiosks`, { headers })).json()).find((item) => item.code === code);
console.log(`${code} is ${now.status}`);

for (const entry of (await (await fetch(`${api}/api/admin/kiosks/${kiosk.id}/commands`, { headers })).json()).slice(0, 8)) {
  console.log(`  ${entry.createdAt}  ${entry.type.padEnd(15)} ${entry.status.padEnd(12)} ${entry.result ?? ''}`);
}
