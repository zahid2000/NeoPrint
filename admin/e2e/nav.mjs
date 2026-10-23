// ---------------------------------------------------------------------------
// The admin menu in groups (T-304, docs/DESIGN_SYSTEM.md 5.3) on the
// development stack, as each kind of account has it: the seeded SuperAdmin,
// Admin, Operator and Maliyyə, and an Operator who looks after one kiosk -
// on a desktop (the full menu), a tablet (icons) and a phone (a drawer).
//
//   docker compose up -d
//   node e2e/nav.mjs                                  # from admin/
//   node e2e/nav.mjs ../docs/screenshots/step-7/t304
//
// For each: the groups shown and none of them empty; the page's own group open;
// a group shut stays shut after a reload, and its page reopens it; no console
// error. The one-kiosk account it makes is switched off at the end.
// Needs Chrome or Edge on the machine.
// ---------------------------------------------------------------------------
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { launchBrowser } from './pages.mjs';

const base = process.env.ADMIN_URL ?? 'http://localhost:8080';
const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const shots = process.argv[2] ? resolve(process.argv[2]) : null;

const failures = [];
const check = (ok, text) => {
  console.log(`${ok ? 'ok    ' : 'WRONG '} ${text}`);
  if (!ok) failures.push(text);
};

async function signIn(email, secret) {
  const response = await fetch(`${api}/api/auth/admin/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password: secret }),
  });
  if (!response.ok) throw new Error(`Could not sign in as ${email}: ${response.status}.`);
  return (await response.json()).accessToken;
}

async function call(token, method, path, body) {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'Idempotency-Key': randomUUID() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${(await response.text()).slice(0, 200)}`);
  return response.status === 204 ? null : response.json();
}

// The one-kiosk account: the seeded Operator role, scoped to the first kiosk.
const root = await signIn('dev-superadmin@neoprint.local', password);
const [kiosk] = await call(root, 'GET', '/api/admin/kiosks');
const operatorRole = (await call(root, 'GET', '/api/admin/roles')).find((role) => role.name === 'Operator');
const stamp = new Date().toISOString().replace(/\D/g, '').slice(0, 14);
const first = `N1-${randomBytes(9).toString('base64url')}!a`;
const scopedEmail = `nav-check-${stamp}@neoprint.local`;
const scopedPassword = `N2-${randomBytes(9).toString('base64url')}!a`;
const scoped = await call(root, 'POST', '/api/admin/admins', {
  email: scopedEmail,
  fullName: 'Nav check',
  password: first,
  roleIds: [operatorRole.id],
  allKiosks: false,
  kioskIds: [kiosk.id],
});
await call(await signIn(scopedEmail, first), 'POST', '/api/auth/admin/change-password', { currentPassword: first, newPassword: scopedPassword });

const accounts = [
  ['superadmin', 'dev-superadmin@neoprint.local', password],
  ['admin', 'dev-admin@neoprint.local', password],
  ['operator', 'dev-operator@neoprint.local', password],
  ['finance', 'dev-finance@neoprint.local', password],
  ['one-kiosk', scopedEmail, scopedPassword],
];
const screens = [
  ['desktop', { width: 1366, height: 900 }],
  ['tablet', { width: 820, height: 1100 }],
  ['phone', { width: 390, height: 844 }],
];

const browser = await launchBrowser();

const menu = (page) =>
  page.evaluate(() => ({
    groups: [...document.querySelectorAll('[data-nav-group]')].map((g) => ({
      id: g.getAttribute('data-nav-group'),
      open: g.getAttribute('aria-expanded') === 'true',
      label: g.querySelector('span')?.textContent.trim() ?? '',
    })),
    sections: [...document.querySelectorAll('[data-nav-items]')].map((s) => ({
      id: s.getAttribute('data-nav-items'),
      items: s.querySelectorAll('a.shell__nav-item').length,
    })),
    links: document.querySelectorAll('a.shell__nav-item').length,
    labels: document.querySelectorAll('.shell__nav-label').length,
    sideways: document.documentElement.scrollWidth > document.documentElement.clientWidth + 1,
  }));

try {
  for (const [name, email, secret] of accounts) {
    // One sign-in per account - the sign-in limit is ten a minute per address (T-306) -
    // and the same page at each width.
    const context = await browser.newContext({ viewport: screens[0][1], locale: 'az', serviceWorkers: 'block' });
    const page = await context.newPage();
    const errors = [];
    page.on('console', (m) => m.type() === 'error' && !/Failed to load resource/.test(m.text()) && errors.push(m.text().slice(0, 200)));
    page.on('pageerror', (e) => errors.push(String(e).slice(0, 200)));

    await page.goto(`${base}/login`, { waitUntil: 'networkidle' });
    await page.locator('input[type=email]').fill(email);
    await page.locator('input[type=password]').fill(secret);
    await page.locator('button[type=submit]').click();
    await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });

    for (const [screen, viewport] of screens) {
      await page.setViewportSize(viewport);
      errors.length = 0;

      // The admin panel itself - an operator lands in the operator panel first.
      await page.goto(`${base}/kiosks`, { waitUntil: 'networkidle' });
      await page.locator('main h1').first().waitFor({ timeout: 15_000 });
      const label = `${name}, ${screen}`;

      if (screen === 'phone') {
        check(!(await page.locator('mat-sidenav').evaluate((n) => n.classList.contains('mat-drawer-opened'))), `${label}: the menu is shut until asked for`);
        await page.getByRole('button', { name: /Menyunu/ }).click();
        await page.waitForTimeout(600);
      }

      const seen = await menu(page);
      if (screen === 'tablet') {
        check(seen.labels === 0 && seen.groups.length === 0 && seen.links > 0, `${label}: icons only (${seen.links}), no names and no group headings`);
      } else {
        check(seen.sections.every((s) => s.items > 0), `${label}: no empty group — ${seen.groups.map((g) => g.label).join(', ')}`);
        check(seen.groups.find((g) => g.id === 'monitoring')?.open !== false, `${label}: the group of the page on screen (Monitorinq) is open`);
      }
      check(!seen.sideways, `${label}: no sideways scroll`);
      if (shots) {
        mkdirSync(shots, { recursive: true });
        await page.screenshot({ path: join(shots, `${name}-${screen}.png`) });
      }

      // Shut a group that is not the page's, reload: still shut; open its page: open again.
      if (screen === 'desktop') {
        const other = seen.groups.find((g) => g.id !== 'monitoring');
        if (other) {
          await page.locator(`[data-nav-group="${other.id}"]`).click();
          await page.reload({ waitUntil: 'networkidle' });
          const after = await menu(page);
          check(after.groups.find((g) => g.id === other.id)?.open === false, `${label}: "${other.label}" stays shut after a reload`);
          const href = await page.evaluate(() => [...document.querySelectorAll('a.shell__nav-item')].map((a) => a.getAttribute('href')));
          const section = (await page.evaluate((id) => [...document.querySelectorAll(`[data-nav-items="${id}"] a`)].length, other.id));
          check(section === 0, `${label}: its pages are hidden while it is shut (${href.length} links shown)`);
          await page.locator(`[data-nav-group="${other.id}"]`).click();
          const firstPage = await page.locator(`[data-nav-items="${other.id}"] a.shell__nav-item`).first().getAttribute('href');
          await page.locator(`[data-nav-group="${other.id}"]`).click();
          await page.goto(`${base}${firstPage}`, { waitUntil: 'networkidle' });
          check((await menu(page)).groups.find((g) => g.id === other.id)?.open === true, `${label}: on ${firstPage} its group opens by itself`);
          await page.locator(`[data-nav-group="${other.id}"]`).click();
          await page.locator(`[data-nav-group="${other.id}"]`).click();
        }
      }

      check(errors.length === 0, `${label}: no console error${errors.length ? `: ${errors.join(' | ')}` : ''}`);
    }

    await context.close();
  }
} finally {
  await browser.close();
  await call(root, 'POST', `/api/admin/admins/${scoped.id}/active`, { isActive: false, reason: 'nav check finished' }).catch(() => undefined);
}

console.log(failures.length ? `\n${failures.length} wrong` : '\nall as expected');
process.exit(failures.length ? 1 : 0);
