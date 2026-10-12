#!/usr/bin/env node
// Reads or saves one platform setting as the development SuperAdmin - what
// the panel's "Tənzimləmələr" page does (plan section 2.4).
//
//   node tools/dev-kiosk/setting.mjs                                  # every setting that is not at its default
//   node tools/dev-kiosk/setting.mjs Notification.MinSeverity          # one setting
//   node tools/dev-kiosk/setting.mjs Notification.MinSeverity Critical # save it
//   node tools/dev-kiosk/setting.mjs PrintJob.MaxAutoRetries 3         # ... and put it back
//
// Needs the compose stack up and the development accounts of
// docs/DEV_ENVIRONMENT.md section 4. No dependencies: Node 20 or later.

const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
const email = process.env.NEOPRINT_DEV_SUPERADMIN_EMAIL ?? 'dev-superadmin@neoprint.local';
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const [key, value] = process.argv.slice(2);

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

const headers = {
  Authorization: `Bearer ${(await login.json()).accessToken}`,
  'Accept-Language': 'az',
  'Content-Type': 'application/json',
};

const show = (setting) =>
  console.log(`${setting.key} = ${setting.value}${setting.isDefault ? '' : `   (default ${setting.defaultValue})`}`);

if (value !== undefined) {
  const saved = await fetch(`${api}/api/admin/settings`, {
    method: 'PUT',
    headers,
    body: JSON.stringify({ values: { [key]: value } }),
  });
  const answer = await saved.json().catch(() => ({}));

  if (!saved.ok) {
    const reasons = Object.values(answer.errors ?? {}).flat().join(' ');
    console.error(`Not saved: ${saved.status} ${reasons || answer.detail || answer.title || ''}`);
    process.exit(1);
  }

  show(answer.find((setting) => setting.key === key));
  process.exit(0);
}

const settings = await (await fetch(`${api}/api/admin/settings`, { headers })).json();

if (key) {
  const setting = settings.find((each) => each.key === key);

  if (!setting) {
    console.error(`There is no setting "${key}".`);
    process.exit(1);
  }

  show(setting);
  console.log(`  ${setting.description}`);
} else {
  const changed = settings.filter((setting) => !setting.isDefault);
  console.log(changed.length === 0 ? `All ${settings.length} settings are at their defaults.` : `${changed.length} of ${settings.length} settings are not at their defaults:`);
  changed.forEach(show);
}
