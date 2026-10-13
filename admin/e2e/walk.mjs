// ---------------------------------------------------------------------------
// Walks every page of the panel on the development stack and writes down what
// is wrong with each (T-257): a console error, a request the server refused, a
// raw translation key, a page that is still a skeleton, an error banner, an
// empty page, a sideways scroll.
//
//   docker compose up -d                       # the stack
//   node e2e/walk.mjs                          # from admin/; the development SuperAdmin, in az
//   node e2e/walk.mjs --lang=ru                # another language
//   node e2e/walk.mjs --scoped                 # an account that sees one kiosk and little else
//   node e2e/walk.mjs --shots=../docs/screenshots/step-7/walk
//
// The render test (npm run e2e) does the same against recorded answers, in
// CI; this is the real API with whatever the stack holds today. With --scoped
// an account is made for the run - a role that may see kiosks, incidents and
// supplies, scoped to the first kiosk, a random password - and switched off at
// the end: a page it may not open must send it away or say so, not hang.
//
// Every page is opened by its address, which is a full reload: the session is
// restored and the live connection opened again each time. A person does that
// a few times an hour; done back to back it runs into the API's limit of 120
// requests a minute per caller, and the panel is sent to the sign-in page. So
// the walk takes at least five seconds a page.
//
// Needs Chrome or Edge on the machine (playwright-core brings no browser).
// ---------------------------------------------------------------------------
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { findRawKeys, languageKey, launchBrowser, menuRoutes } from './pages.mjs';

const base = process.env.ADMIN_URL ?? 'http://localhost:8080';
const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
const adminEmail = process.env.NEOPRINT_DEV_SUPERADMIN_EMAIL ?? 'dev-superadmin@neoprint.local';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const adminPassword = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const option = (name) => process.argv.find((arg) => arg.startsWith(`--${name}=`))?.split('=')[1];
const scoped = process.argv.includes('--scoped');
const language = option('lang') ?? 'az';
const shots = option('shots') ? resolve(option('shots')) : null;

async function call(token, method, path, body) {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'Idempotency-Key': randomUUID() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${(await response.text()).slice(0, 200)}`);

  return response.status === 204 ? null : response.json();
}

async function signIn(email, password) {
  const response = await fetch(`${api}/api/auth/admin/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  });

  if (!response.ok) throw new Error(`Could not sign in as ${email}: ${response.status}.`);

  return (await response.json()).accessToken;
}

const root = await signIn(adminEmail, adminPassword);
const kiosks = await call(root, 'GET', '/api/admin/kiosks');
const firstKiosk = kiosks[0];

let who = adminEmail;
let secret = adminPassword;
let account = null;

if (scoped) {
  if (!firstKiosk) throw new Error('The stack has no kiosk to scope an account to.');

  const stamp = new Date().toISOString().replace(/\D/g, '').slice(0, 14);
  const role = await call(root, 'POST', '/api/admin/roles', {
    name: `Walk check ${stamp}`,
    description: 'Made by admin/e2e/walk.mjs; safe to delete.',
    permissions: ['kiosks.view', 'incidents.view', 'supplies.view'],
  });
  const first = `W1-${randomBytes(9).toString('base64url')}!a`;

  who = `walk-check-${stamp}@neoprint.local`;
  secret = `W2-${randomBytes(9).toString('base64url')}!a`;
  account = await call(root, 'POST', '/api/admin/admins', {
    email: who,
    fullName: 'Walk check',
    password: first,
    roleIds: [role.id],
    allKiosks: false,
    kioskIds: [firstKiosk.id],
  });
  await call(await signIn(who, first), 'POST', '/api/auth/admin/change-password', { currentPassword: first, newPassword: secret });
}

const browser = await launchBrowser();
const context = await browser.newContext({ viewport: { width: 1366, height: 900 }, locale: language, serviceWorkers: 'block' });
await context.addInitScript(([key, value]) => localStorage.setItem(key, value), [languageKey, language]);
const page = await context.newPage();

// No faster than a page every five seconds - see the note at the top.
const pageMs = 5000;

// The live connection carries its token in its address; nothing printed here may hold one.
const withoutTokens = (text) => text.replace(/access_token=[^&\s'"]+/g, 'access_token=…').replace(/eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/g, '…');

let current = '';
const problems = new Map();
// Nothing is written down before the first page: the sign-in page asking for a session that is not there yet is no fault.
const note = (text) => {
  if (current) problems.set(current, [...(problems.get(current) ?? []), withoutTokens(text)]);
};

page.on('console', (message) => {
  if (message.type() !== 'error') return;
  const text = message.text();
  // A refused request is reported below with its address; the browser's own line about it adds nothing.
  if (/Failed to load resource/.test(text)) return;
  note(`console: ${text.slice(0, 220)}`);
});
page.on('pageerror', (error) => note(`exception: ${String(error).slice(0, 220)}`));
page.on('response', (response) => {
  const url = new URL(response.url());
  if (response.status() >= 400 && (url.pathname.startsWith('/api/') || url.pathname.startsWith('/hubs/'))) {
    note(`${response.status()} ${response.request().method()} ${url.pathname}`);
  }
});

await page.goto(`${base}/login`, { waitUntil: 'networkidle' });
await page.locator('input[type=email]').fill(who);
await page.locator('input[type=password]').fill(secret);
await page.locator('button[type=submit]').click();
await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });

// The menu's pages, and the detail pages a list leads to.
const routes = [...menuRoutes];
if (firstKiosk) routes.push(`kiosks/${firstKiosk.id}`);

const visited = [];

try {
  for (const route of routes) {
    current = `/${route}`;
    const opened = Date.now();
    await page.goto(`${base}/${route}`, { waitUntil: 'networkidle', timeout: 30_000 }).catch((error) => note(`did not load: ${String(error).split('\n')[0]}`));
    await page.waitForTimeout(1200);

    const landed = new URL(page.url()).pathname;
    const state = await page.evaluate(() => {
      const main = document.querySelector('main') ?? document.body;
      return {
        skeletons: document.querySelectorAll('.neo-skeleton').length,
        banners: [...document.querySelectorAll('app-error-banner, .neo-field-error[role=alert]')].map((element) => element.textContent.replace(/\s+/g, ' ').trim().slice(0, 140)),
        heading: (document.querySelector('h1')?.textContent ?? '').trim(),
        words: main.innerText.trim().length,
        sideways: document.documentElement.scrollWidth > document.documentElement.clientWidth + 1,
      };
    });
    const rawKeys = await page.evaluate(findRawKeys);

    if (landed !== current && !(route === '' && landed === '/dashboard')) {
      // Sent elsewhere: fine for an account that may not open the page, a fault for one that may.
      if (!scoped) note(`sent to ${landed}`);
    } else {
      if (state.skeletons > 0) note(`still loading: ${state.skeletons} skeleton(s) after the network went quiet`);
      if (!state.heading) note('no heading');
      if (state.words < 40) note(`nearly empty: ${state.words} characters`);
      if (state.sideways) note('scrolls sideways');
      state.banners.forEach((banner) => note(`error shown: ${banner}`));
      rawKeys.forEach((key) => note(`raw key: ${key}`));
    }

    visited.push({ route: current, landed, heading: state.heading });

    if (shots) {
      mkdirSync(shots, { recursive: true });
      await page.screenshot({ path: join(shots, `${scoped ? 'scoped' : 'admin'}-${language}-${(route || 'dashboard').replace(/\//g, '-').replace(/[0-9a-f]{8}-[0-9a-f-]{27}/, 'detail')}.png`) });
    }

    await page.waitForTimeout(Math.max(0, pageMs - (Date.now() - opened)));
  }

  // The kiosk detail's tabs: each loads its own data when it is opened.
  if (firstKiosk) {
    current = `/kiosks/${firstKiosk.id}`;
    await page.goto(`${base}${current}`, { waitUntil: 'networkidle' });
    const tabs = page.locator('[role=tab]');
    const count = await tabs.count();

    for (let index = 0; index < count; index++) {
      const name = (await tabs.nth(index).innerText()).trim();
      current = `/kiosks/{id} → ${name}`;
      await tabs.nth(index).click();
      await page.waitForLoadState('networkidle').catch(() => undefined);
      await page.waitForTimeout(900);

      const skeletons = await page.locator('.neo-skeleton').count();
      if (skeletons > 0) note(`still loading: ${skeletons} skeleton(s)`);
      (await page.evaluate(findRawKeys)).forEach((key) => note(`raw key: ${key}`));
      visited.push({ route: current, landed: current, heading: name });

      if (shots) await page.screenshot({ path: join(shots, `${scoped ? 'scoped' : 'admin'}-${language}-kiosk-tab-${index + 1}.png`) });
    }
  }
} finally {
  await browser.close();

  if (account) {
    await call(root, 'POST', `/api/admin/admins/${account.id}/active`, { isActive: false, reason: 'walk check finished' }).catch(() => undefined);
  }
}

console.log(`${scoped ? 'scoped account' : 'SuperAdmin'}, ${language}: ${visited.length} pages and tabs`);
for (const { route, landed, heading } of visited) {
  const found = problems.get(route) ?? [];
  const moved = landed !== route && !route.includes('→') ? ` → ${landed}` : '';
  console.log(`${found.length === 0 ? 'ok    ' : 'WRONG '} ${route}${moved}${heading ? `  "${heading}"` : ''}`);
  [...new Set(found)].forEach((line) => console.log(`         ${line}`));
}

const total = [...problems.values()].reduce((sum, list) => sum + new Set(list).size, 0);
console.log(total === 0 ? '\nNothing wrong on any page.' : `\n${total} problem(s).`);
process.exit(total === 0 ? 0 : 1);
