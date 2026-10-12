// ---------------------------------------------------------------------------
// Watches what the panel shows, without a reload, while a kiosk of the
// development stack goes away and comes back (T-248).
//
//   docker compose up -d                       # the stack and a running kiosk
//   node e2e/live.mjs                          # from admin/; KSK-9002 in neoprint-kshot
//   node e2e/live.mjs KSK-9002 neoprint-kshot ../docs/screenshots/step-7/live
//   node e2e/live.mjs --connection             # the panel's own connection is cut instead
//
// Two tabs stay open - the kiosk list and the dashboard - and are never
// reloaded. The kiosk's container is stopped, then started; every three
// seconds the script writes down what the API says, what each tab shows and
// which hub events arrived. The last line says whether the tabs followed.
//
// With --connection the kiosk stays up and the panel loses its live
// connection: the hub is blocked, the kiosk is paused meanwhile (the event is
// never heard), and the tabs have to find out by asking - every 30 seconds
// while cut off, and at once when the connection is back.
//
// Needs Chrome or Edge on the machine (playwright-core brings no browser).
// ---------------------------------------------------------------------------
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { launchBrowser } from './pages.mjs';

const base = process.env.ADMIN_URL ?? 'http://localhost:8080';
const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
const email = process.env.NEOPRINT_DEV_ADMIN_EMAIL ?? 'dev-admin@neoprint.local';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const args = process.argv.slice(2).filter((arg) => !arg.startsWith('--'));
const cutConnection = process.argv.includes('--connection');
const code = args[0] ?? 'KSK-9002';
const container = args[1] ?? 'neoprint-kshot';
const shots = args[2] ? resolve(args[2]) : null;
// The container whose nginx stands between the panel and the API.
const proxy = process.env.ADMIN_CONTAINER ?? 'neoprint-admin';

const started = Date.now();
const at = () => `${String(Math.round((Date.now() - started) / 1000)).padStart(4)}s`;
const events = [];
const consoleErrors = [];

const browser = await launchBrowser();
const context = await browser.newContext({ viewport: { width: 1366, height: 900 }, locale: 'az' });

/** A tab that records its hub frames and its console errors. */
async function tab(name) {
  const page = await context.newPage();

  page.on('websocket', (socket) => {
    events.push(`${at()} ${name}: websocket opened ${new URL(socket.url()).pathname}`);
    socket.on('close', () => events.push(`${at()} ${name}: websocket closed`));
    socket.on('framereceived', ({ payload }) => {
      // SignalR's JSON protocol: messages end with the record separator; type 1 is an invocation, 6 a ping.
      for (const text of String(payload).split('\u001e').filter(Boolean)) {
        try {
          const message = JSON.parse(text);
          if (message.type === 1) {
            events.push(`${at()} ${name}: ${message.target} ${JSON.stringify(message.arguments?.[0] ?? {}).slice(0, 110)}`);
          }
        } catch {
          // Not a message of ours.
        }
      }
    });
  });

  page.on('console', (message) => {
    if (message.type() === 'error') consoleErrors.push(`${at()} ${name}: ${message.text().slice(0, 200)}`);
  });

  return page;
}

const list = await tab('list');

await list.goto(`${base}/login`, { waitUntil: 'networkidle' });
await list.locator('input[type=email]').fill(email);
await list.locator('input[type=password]').fill(password);
await list.locator('button[type=submit]').click();
await list.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });
await list.goto(`${base}/kiosks`, { waitUntil: 'networkidle' });

const dashboard = await tab('dashboard');
await dashboard.goto(`${base}/dashboard`, { waitUntil: 'networkidle' });
await dashboard.waitForTimeout(2000);

// The API's own answer, asked apart from the tabs so that watching does not refresh them.
const login = await fetch(`${api}/api/auth/admin/login`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email, password }),
});

if (!login.ok) {
  console.error(`Could not sign in to the API: ${login.status}.`);
  await browser.close();
  process.exit(2);
}

const headers = { Authorization: `Bearer ${(await login.json()).accessToken}` };

async function theKiosk() {
  const kiosks = await (await fetch(`${api}/api/admin/kiosks`, { headers })).json();
  return kiosks.find((kiosk) => kiosk.code === code);
}

const serverStatus = async () => (await theKiosk())?.status ?? '?';

/** The status chip in the kiosk's row of the list, as the administrator reads it. */
const listShows = () =>
  list.evaluate((wanted) => {
    const row = [...document.querySelectorAll('tr')].find((tr) => tr.textContent.includes(wanted));
    const chip = row?.querySelector('app-status-chip');
    // The chip is an icon and a word; the icon's ligature is not what is read.
    return chip ? ([...chip.querySelectorAll('span')].at(-1)?.textContent.trim() ?? chip.textContent.trim()) : '(no row)';
  }, code);

/** The dashboard's kiosk counts ("Onlayn: 1, Oflayn: 2") and the header's "live" label. */
const dashboardShows = () =>
  dashboard.evaluate(() => {
    const word = (element) => [...element.querySelectorAll('span')].at(-1)?.textContent.trim() ?? '';
    const chips = [...document.querySelectorAll('.dashboard__statuses app-status-chip')].map(word);
    const indicator = document.querySelector('app-live-indicator .live');
    const live = indicator ? [...indicator.children].find((child) => child.tagName === 'SPAN')?.textContent.trim() : '';
    return `${chips.join(', ') || '(no counts)'}${live ? ` [${live}]` : ''}`;
  });

async function shot(name) {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });

  // A tab in the background is not painted, and a picture of it never comes.
  for (const [page, suffix] of [[dashboard, 'dashboard'], [list, 'list']]) {
    await page.bringToFront();
    await page.screenshot({ path: join(shots, `${name}-${suffix}.png`) });
  }
}

async function row(note = '') {
  const [server, shown, counts] = [await serverStatus(), await listShows(), await dashboardShows()];
  console.log(`${at()}  api=${server.padEnd(8)} list="${shown}"  dashboard: ${counts}  ${note}`);
  return { server, shown, counts };
}

/** Watches until the API says `wanted`, then a little longer for the tabs to follow. */
async function until(wanted, seconds) {
  const deadline = Date.now() + seconds * 1000;
  let reachedAt = null;
  let last;

  while (Date.now() < deadline) {
    await list.waitForTimeout(3000);
    last = await row();
    if (last.server === wanted) reachedAt ??= Date.now();
    if (reachedAt && Date.now() - reachedAt > 12_000) break;
  }

  return last;
}

const docker = (...args) => execFileSync('docker', args, { stdio: 'ignore' });

function report() {
  console.log('\nHub events the tabs received:');
  for (const line of events) console.log(`  ${line}`);

  console.log(`\nConsole errors: ${consoleErrors.length}`);
  for (const line of consoleErrors) console.log(`  ${line}`);
}

if (cutConnection) {
  const kiosk = await theKiosk();
  const command = (name) => fetch(`${api}/api/admin/kiosks/${kiosk.id}/${name}`, { method: 'POST', headers });

  console.log(`The panel's live connection is cut while ${code} is paused and resumed.\n`);
  const before = await row('<- before');
  await shot('c1-before');

  // The hub cannot be reached; everything else can. An open socket outlives
  // a blocked route, so the proxy in front of the API is restarted: that ends
  // the socket for real, and every attempt to open another is refused.
  console.log(`\n${at()}  the hub is blocked and ${proxy} is restarted (HTTP works again in a moment)`);
  await context.route('**/hubs/**', (route) => route.abort());
  docker('restart', proxy);

  for (let attempt = 0; attempt < 30; attempt++) {
    if (await fetch(base).then((response) => response.ok, () => false)) break;
    await list.waitForTimeout(1000);
  }

  await list.waitForTimeout(4000);
  await row('<- cut off');

  console.log(`\n${at()}  POST kiosks/${code}/pause - the event is not heard`);
  await command('pause');
  const pausedAt = Date.now();
  let paused;

  // Nothing but asking can tell the tabs: within one polling interval, and a little.
  while (Date.now() - pausedAt < 45_000) {
    await list.waitForTimeout(3000);
    paused = await row();
    if (paused.shown !== before.shown) break;
  }

  const polled = paused.shown !== before.shown;
  const pollSeconds = Math.round((Date.now() - pausedAt) / 1000);
  await shot('c2-cut-off-paused');

  console.log(`\n${at()}  POST kiosks/${code}/resume, and the hub is reachable again`);
  await command('resume');
  await context.unroute('**/hubs/**');
  const resumedAt = Date.now();
  let resumed;

  while (Date.now() - resumedAt < 75_000) {
    await list.waitForTimeout(3000);
    resumed = await row();
    if (resumed.shown === before.shown && resumed.counts.includes('[Canlı]')) break;
  }

  const back = resumed.shown === before.shown && resumed.counts.includes('[Canlı]');
  await shot('c3-connected-again');
  report();

  console.log(
    polled && back
      ? `\nCut off, the list found the pause by asking, ${pollSeconds} s after it ("${before.shown}" -> "${paused.shown}"); connected again it shows "${resumed.shown}" and is live.`
      : `\nNOT followed: cut off the list showed "${paused.shown}" (asked for ${pollSeconds} s); afterwards "${resumed.shown}", ${resumed.counts}.`,
  );

  await browser.close();
  process.exit(polled && back ? 0 : 1);
}

console.log(`Watching ${code} (${container}) in two tabs that are never reloaded.\n`);
const first = await row('<- before');
await shot('1-before');

console.log(`\n${at()}  docker stop ${container}`);
docker('stop', container);
const offline = await until('Offline', 150);
await shot('2-offline');

console.log(`\n${at()}  docker start ${container}`);
docker('start', container);
const online = await until(first.server, 120);
await shot('3-back');

report();

const followed = offline.shown !== first.shown && online.shown === first.shown;
console.log(
  followed
    ? `\nThe list followed without a reload: "${first.shown}" -> "${offline.shown}" -> "${online.shown}".`
    : `\nThe list did NOT follow: before "${first.shown}", after the stop "${offline.shown}" (api ${offline.server}), after the start "${online.shown}" (api ${online.server}).`,
);

await browser.close();
process.exit(followed ? 0 : 1);
