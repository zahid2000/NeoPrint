// ---------------------------------------------------------------------------
// Leaves the panel open until its access token runs out, and checks that it
// stays live across that (T-249, T-256).
//
//   docker compose up -d                       # the stack and a running kiosk
//   node e2e/token.mjs                         # from admin/; KSK-9002
//   node e2e/token.mjs KSK-9002 ../docs/screenshots/step-7/t256
//
// The access token lives fifteen minutes and the server ends a hub connection
// when the token it was opened with runs out - the kiosks an account hears
// about were decided from that token. The panel then has to exchange the token
// and connect again by itself. This script signs in, opens the kiosk list and
// waits, never reloading: it writes down when the socket was closed and when
// the next one opened, and then pauses and resumes the kiosk through the API
// to see that the row still follows.
//
// "It connected again" is not the test. The first run of this script found
// the panel connecting once a second for half a minute after the token ran out
// (T-262): the server still answered requests with the old token, the panel
// took that for a good token, and every connection opened with it was closed
// at once. So after the server ends the connection the script watches for
// forty-five seconds - longer than the server's tolerance for an old token -
// and wants exactly one new connection, still open, a token exchange before
// it, and no request refused for coming too often.
//
// The lifetime is a constant of the backend, not a setting, so the wait is the
// real fifteen minutes; the whole run takes about seventeen.
//
// Needs Chrome or Edge on the machine (playwright-core brings no browser).
// ---------------------------------------------------------------------------
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { launchBrowser } from './pages.mjs';

const base = process.env.ADMIN_URL ?? 'http://localhost:8080';
const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
const email = process.env.NEOPRINT_DEV_ADMIN_EMAIL ?? 'dev-superadmin@neoprint.local';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const args = process.argv.slice(2).filter((arg) => !arg.startsWith('--'));
const code = args[0] ?? 'KSK-9002';
const shots = args[1] ? resolve(args[1]) : null;
// How long to wait for the server to end the connection: the token's life and a margin.
const waitMinutes = Number(process.env.TOKEN_WAIT_MINUTES ?? 17);

const started = Date.now();
const seconds = () => Math.round((Date.now() - started) / 1000);
const at = () => `${String(seconds()).padStart(4)}s`;
const log = (line) => console.log(`${at()} ${line}`);

const sockets = [];
const consoleErrors = [];
const refreshes = [];
const tooMany = [];

// How long the panel is watched after the server ended its connection: longer than the
// thirty seconds for which the API still answers with a token that has run out.
const settleSeconds = 45;

const browser = await launchBrowser();
const context = await browser.newContext({ viewport: { width: 1366, height: 900 }, locale: 'az', serviceWorkers: 'block' });
const page = await context.newPage();

page.on('websocket', (socket) => {
  const entry = { openedAt: seconds(), closedAt: null, events: 0 };
  sockets.push(entry);
  log(`websocket #${sockets.length} opened ${new URL(socket.url()).pathname}`);

  socket.on('close', () => {
    entry.closedAt = seconds();
    log(`websocket #${sockets.indexOf(entry) + 1} closed after ${entry.closedAt - entry.openedAt}s`);
  });

  socket.on('framereceived', ({ payload }) => {
    for (const text of String(payload).split('\u001e').filter(Boolean)) {
      try {
        const message = JSON.parse(text);
        if (message.type === 1) {
          entry.events++;
          log(`  #${sockets.indexOf(entry) + 1} ${message.target} ${JSON.stringify(message.arguments?.[0] ?? {}).slice(0, 90)}`);
        }
      } catch {
        // Not a message of ours.
      }
    }
  });
});

page.on('request', (request) => {
  if (request.url().includes('/api/auth/admin/refresh')) {
    refreshes.push(seconds());
    log('the panel exchanges its token (POST /api/auth/admin/refresh)');
  }
});

page.on('response', (response) => {
  if (response.status() === 429) {
    tooMany.push(seconds());
    log(`429 ${response.request().method()} ${new URL(response.url()).pathname}`);
  }
});

page.on('console', (message) => {
  if (message.type() === 'error') consoleErrors.push(`${at()} ${message.text().slice(0, 200)}`);
});

await page.goto(`${base}/login`, { waitUntil: 'networkidle' });
await page.locator('input[type=email]').fill(email);
await page.locator('input[type=password]').fill(password);
await page.locator('button[type=submit]').click();
await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });
const signedInAt = seconds();
await page.goto(`${base}/kiosks`, { waitUntil: 'networkidle' });
log(`signed in at ${signedInAt}s; the kiosk list is open and will not be reloaded`);

/** The status chip in the kiosk's row, as the administrator reads it. */
const rowShows = () =>
  page.evaluate((wanted) => {
    const row = [...document.querySelectorAll('tr')].find((tr) => tr.textContent.includes(wanted));
    const chip = row?.querySelector('app-status-chip');
    return chip ? ([...chip.querySelectorAll('span')].at(-1)?.textContent.trim() ?? chip.textContent.trim()) : '(no row)';
  }, code);

/** The header's "live" label. */
const indicator = () =>
  page.evaluate(() => {
    const live = document.querySelector('app-live-indicator .live');
    return live ? ([...live.children].find((child) => child.tagName === 'SPAN')?.textContent.trim() ?? '') : '(none)';
  });

async function shot(name) {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  await page.bringToFront();
  await page.screenshot({ path: join(shots, `${name}.png`) });
}

async function until(label, check, timeoutMs, everyMs = 1000) {
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    if (await check()) return true;
    await page.waitForTimeout(everyMs);
  }

  log(`gave up waiting for ${label}`);
  return false;
}

const before = await rowShows();
log(`row: ${before}; indicator: ${await indicator()}`);

// --- the wait ---------------------------------------------------------------
const deadline = Date.now() + waitMinutes * 60_000;
let lastNote = 0;

while (Date.now() < deadline && !(sockets.length >= 1 && sockets[0].closedAt !== null)) {
  await page.waitForTimeout(5000);

  if (seconds() - lastNote >= 120) {
    lastNote = seconds();
    log(`waiting … row: ${await rowShows()}; indicator: ${await indicator()}`);
  }
}

const first = sockets[0];
const closedAfter = first?.closedAt === null || !first ? null : first.closedAt - signedInAt;

// What the panel does next, for longer than an old token is still answered.
if (closedAfter !== null) {
  log(`the server ended the connection; watching for ${settleSeconds}s`);
  await page.waitForTimeout(settleSeconds * 1000);
}

const second = sockets[1];
const reopened = second && first.closedAt !== null ? second.openedAt - first.closedAt : null;
const exchanged = first?.closedAt === null || !first ? [] : refreshes.filter((at) => at >= first.closedAt - 2);
const steady = sockets.length === 2 && second.closedAt === null;

log(`${settleSeconds}s after the token ran out - ${sockets.length - 1} new connection(s); row: ${await rowShows()}; indicator: ${await indicator()}`);

// --- still live? -------------------------------------------------------------
// A new sign-in for the script itself: its own token would have run out as well.
async function apiHeaders() {
  const login = await fetch(`${api}/api/auth/admin/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  });

  if (!login.ok) throw new Error(`Could not sign in to the API: ${login.status}.`);

  return { Authorization: `Bearer ${(await login.json()).accessToken}` };
}

let followedPause = false;
let followedResume = false;

if (second) {
  const headers = await apiHeaders();
  const kiosk = (await (await fetch(`${api}/api/admin/kiosks`, { headers })).json()).find((item) => item.code === code);

  if (!kiosk) {
    log(`no kiosk ${code} on the stack`);
  } else {
    const paused = await fetch(`${api}/api/admin/kiosks/${kiosk.id}/pause`, { method: 'POST', headers });
    log(`pause sent through the API: ${paused.status}`);
    followedPause = await until('the row to change', async () => (await rowShows()) !== before, 15_000);
    log(`row after the pause: ${await rowShows()}`);
    await shot('01-paused-after-token-expiry');

    const resumed = await fetch(`${api}/api/admin/kiosks/${kiosk.id}/resume`, { method: 'POST', headers });
    log(`resume sent through the API: ${resumed.status}`);
    followedResume = await until('the row to come back', async () => (await rowShows()) === before, 30_000);
    log(`row after the resume: ${await rowShows()}`);
    await shot('02-resumed');
  }
}

await browser.close();

console.log('\n--- result ---');
console.log(`connection closed by the server ${closedAfter ?? '?'}s after sign-in (the token lives 900s)`);
console.log(`next connection opened ${reopened ?? '?'}s later; connections opened after that: ${Math.max(0, sockets.length - 2)} (0 is right)`);
console.log(`token exchanged before reconnecting: ${exchanged.length > 0} (${exchanged.length} exchange(s)); requests refused with 429: ${tooMany.length}`);
console.log(`row followed a pause after that: ${followedPause}; and the resume: ${followedResume}`);
console.log(`console errors: ${consoleErrors.length}`);
consoleErrors.forEach((line) => console.log(`  ${line}`));

const ok =
  closedAfter !== null &&
  closedAfter >= 840 &&
  closedAfter <= 960 &&
  reopened !== null &&
  reopened <= 15 &&
  steady &&
  exchanged.length > 0 &&
  tooMany.length === 0 &&
  followedPause &&
  followedResume;

console.log(ok ? 'OK: the panel stayed live across the token running out.' : 'FAILED: see above.');
process.exit(ok ? 0 : 1);
