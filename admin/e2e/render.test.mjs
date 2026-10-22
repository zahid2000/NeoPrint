// ---------------------------------------------------------------------------
// Every page of the panel, rendered in a real browser in az, en and ru
// (docs/DESIGN_SYSTEM.md section 6):
//
//   - nothing on it is a raw translation key;
//   - it has its heading and no "could not load" banner;
//   - it does not scroll sideways at 1366 px;
//   - a picture of it is saved (e2e/screenshots, an artifact in CI).
//
//   npx ng build --configuration production      # dist/admin/browser
//   node --test e2e/render.test.mjs                             # from admin/
//
// No backend is needed: the API is played back from fixtures/api.json, which
// e2e/record.mjs makes from the development stack. The static half of the
// same rule - every key in a dictionary, every server value named - is
// scripts/i18n-check.mjs.
// ---------------------------------------------------------------------------
import assert from 'node:assert/strict';
import { createReadStream, existsSync, mkdirSync, readFileSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, normalize } from 'node:path';
import { after, before, test } from 'node:test';

import { findRawKeys, languageKey, languages, launchBrowser } from './pages.mjs';

const here = new URL('.', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');
const dist = join(here, '..', 'dist', 'admin', 'browser');
const shots = join(here, 'screenshots');
const recorded = JSON.parse(readFileSync(join(here, 'fixtures', 'api.json'), 'utf8'));

// The operator panel's answers are written by hand (fixtures/operator.json): they
// are not part of what record.mjs takes from a stack, and recording again leaves
// them alone.
const operator = JSON.parse(readFileSync(join(here, 'fixtures', 'operator.json'), 'utf8'));
const api = { ...recorded.api, ...operator.api };
const routes = [...recorded.routes, ...operator.routes];

/** The operator panel is made for a phone (plan section 8B): it is also looked at as one. */
const phone = { width: 390, height: 844 };

const types = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.json': 'application/json',
  '.woff2': 'font/woff2', '.svg': 'image/svg+xml', '.png': 'image/png', '.ico': 'image/x-icon', '.txt': 'text/plain',
};

let server;
let browser;
let origin;

before(async () => {
  assert.ok(existsSync(join(dist, 'index.html')), 'Build the panel first: npx ng build --configuration production');
  mkdirSync(shots, { recursive: true });

  // The built panel, with every unknown path answered by index.html as nginx does.
  server = createServer((request, response) => {
    const path = normalize(decodeURIComponent(new URL(request.url, 'http://x').pathname)).replace(/^([\\/])+/, '');
    let file = join(dist, path);

    if (!file.startsWith(dist) || !existsSync(file) || statSync(file).isDirectory()) file = join(dist, 'index.html');

    response.writeHead(200, { 'Content-Type': types[extname(file)] ?? 'application/octet-stream' });
    createReadStream(file).pipe(response);
  });

  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
  browser = await launchBrowser();
});

after(async () => {
  await browser?.close();
  await new Promise((resolve) => server?.close(resolve));
});

const passes = [
  { name: 'every page', viewport: { width: 1366, height: 900 }, routes, suffix: '' },
  { name: 'the operator panel on a phone', viewport: phone, routes: operator.routes, suffix: '-phone' },
];

for (const pass of passes) for (const language of languages) {
  test(`${pass.name} shows words, not keys (${language})`, { timeout: 300_000 }, async () => {
    const context = await browser.newContext({ viewport: pass.viewport, locale: language, serviceWorkers: 'block' });
    await context.addInitScript(([key, value]) => localStorage.setItem(key, value), [languageKey, language]);
    // A browser that has signed in before keeps a sign of it (T-288): without it the
    // panel asks for no session at all and every page opens the sign-in.
    await context.addInitScript(() => localStorage.setItem('neoprint.admin.session', '1'));

    const unanswered = new Set();

    await context.route('**/hubs/**', (route) => route.abort());
    // Nothing leaves this machine: the map's host (T-217) is refused at once, so the
    // map shows its notice - which is checked for words like any other text.
    await context.route((url) => !url.href.startsWith(origin), (route) => route.abort());
    await context.route('**/api/**', (route) => {
      const request = route.request();
      const url = new URL(request.url());
      const body = api[`${request.method()} ${url.pathname}${url.search}`] ?? api[`${request.method()} ${url.pathname}`];

      if (body === undefined) {
        if (request.method() === 'GET') unanswered.add(`GET ${url.pathname}`);
        return route.fulfill({ status: 404, contentType: 'application/problem+json', body: '{"title":"Not recorded","status":404}' });
      }

      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    });

    const page = await context.newPage();
    const problems = [];

    for (const route of pass.routes) {
      const name = route === '' ? 'dashboard' : route.replace(/[0-9a-f]{8}-[0-9a-f-]{27}/g, 'id').replace(/\//g, '_');

      await page.goto(`${origin}/${route}`, { waitUntil: 'networkidle' });
      await page.waitForTimeout(300);

      // A page the recorded administrator may not open is sent back to the dashboard:
      // without this every check below would pass on the wrong page.
      const landed = new URL(page.url()).pathname.slice(1);
      if (landed !== route) problems.push(`/${route}: opened /${landed} instead`);

      const keys = await page.evaluate(findRawKeys);
      if (keys.length > 0) problems.push(`/${route}: raw key(s) on the page: ${keys.join(', ')}`);

      const heading = (await page.locator('h1').first().textContent({ timeout: 5000 }).catch(() => null))?.trim();
      if (!heading) problems.push(`/${route}: no heading - the page did not render`);

      if (await page.locator('app-error-banner').first().isVisible().catch(() => false)) {
        problems.push(`/${route}: shows its "could not load" banner`);
      }

      const sideways = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
      if (sideways > 1) problems.push(`/${route}: scrolls sideways by ${sideways} px at ${pass.viewport.width} px`);

      await page.screenshot({ path: join(shots, `${language}-${name}${pass.suffix}.png`) });
    }

    await context.close();

    assert.deepEqual(
      [...unanswered], [],
      'Pages asked the API for something that is in neither fixtures/api.json (record again: node e2e/record.mjs) nor fixtures/operator.json (written by hand)');
    assert.deepEqual(problems, []);
  });
}
