// ---------------------------------------------------------------------------
// Step 7's end-to-end update check, through the panel (step 7B, M18 - T-326):
// on the Linux container kiosk (tools/dev-kiosk, docs/DEV_ENVIRONMENT.md 7j-1)
// a new version is uploaded on the "Buraxılışlar" page and rolled out to the
// pilot channel; the kiosk updates itself at its idle moment and goes on
// working without registering again; a version that cannot start is rolled
// back by itself and raises an incident. The kiosk's "Yeniləmə" tab follows it
// live; a log is asked for, with a reason, and downloaded. Pictures at desktop
// and phone width; no console error on any page.
//
//   bash tools/dev-kiosk/make-release.sh app 0.8.0-m18.1             # from the repository
//   bash tools/dev-kiosk/make-release.sh app 0.8.0-m18.2 --broken
//   node e2e/updates.mjs KSK-9007 /tmp/neoprint-releases/app-0.8.0-m18.1 /tmp/neoprint-releases/app-0.8.0-m18.2 \
//        ../docs/screenshots/step-7b/m18                                # from admin/
//
// The kiosk is put in the pilot channel; for the run the update window is the
// whole day and the health check two minutes - both are put back at the end.
// Needs Chrome or Edge.
// ---------------------------------------------------------------------------
import { mkdirSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { launchBrowser } from './pages.mjs';

const base = process.env.ADMIN_URL ?? 'http://localhost:8080';
const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const [code, goodDir, brokenDir, shotsArg] = process.argv.slice(2);
if (!code || !goodDir || !brokenDir) {
  console.error('usage: node e2e/updates.mjs <kioskCode> <good-release-dir> <broken-release-dir> [screenshots-dir]');
  process.exit(2);
}
const shots = shotsArg ? resolve(shotsArg) : null;

const failures = [];
const check = (ok, text) => {
  console.log(`${ok ? 'ok    ' : 'WRONG '} ${text}`);
  if (!ok) failures.push(text);
};

// ---- The API, for what the panel shows to be compared with ----
const signIn = async () => {
  const login = await fetch(`${api}/api/auth/admin/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: 'dev-superadmin@neoprint.local', password }),
  });
  if (!login.ok) throw new Error(`sign-in: ${login.status}`);
  return (await login.json()).accessToken;
};
let token = await signIn();
const call = async (method, path, body, again = true) => {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'Idempotency-Key': crypto.randomUUID() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  // The run is longer than an access token lives: sign in again once.
  if (response.status === 401 && again) {
    token = await signIn();
    return call(method, path, body, false);
  }
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${(await response.text()).slice(0, 200)}`);
  return response.status === 204 ? null : response.json();
};
const setting = async (key, value) => {
  const before = (await call('GET', '/api/admin/settings')).find((item) => item.key === key)?.value;
  await call('PUT', '/api/admin/settings', { values: { [key]: String(value) } });
  return before;
};

const kiosk = (await call('GET', '/api/admin/kiosks')).find((item) => item.code === code);
if (!kiosk) throw new Error(`No kiosk ${code}.`);
const files = (dir) => {
  const names = readdirSync(dir);
  const manifest = names.find((name) => name.endsWith('.manifest.json'));
  return { package: join(dir, manifest.replace(/\.manifest\.json$/, '')), manifest: join(dir, manifest), signature: join(dir, `${manifest}.sig`) };
};
const versionOf = (dir) => readdirSync(dir).find((name) => name.endsWith('.manifest.json')).match(/app-(.+)-linux-x64/)[1];
const good = versionOf(goodDir);
const broken = versionOf(brokenDir);

// "Yenidən qeydiyyatsız": the same device before and after, and no new one.
const devicesBefore = await call('GET', '/api/admin/devices');
const deviceBefore = devicesBefore.find((device) => device.kioskId === kiosk.id && device.status === 'Active')?.id;

const window = await setting('Kiosk.UpdateWindow', '00:00-23:59');
const health = await setting('Update.HealthTimeoutMinutes', 2);

const browser = await launchBrowser();
const consoleErrors = [];
const watch = (page, name) => {
  page.on('console', (message) => message.type() === 'error' && consoleErrors.push(`${name}: ${message.text()}`));
  page.on('pageerror', (error) => consoleErrors.push(`${name}: ${error.message}`));
};

try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, serviceWorkers: 'block', acceptDownloads: true });
  const tab = await context.newPage();
  watch(tab, 'desktop');
  await tab.goto(`${base}/login`, { waitUntil: 'networkidle' });
  await tab.fill('input[type="email"]', 'dev-superadmin@neoprint.local');
  await tab.fill('input[type="password"]', password);
  await tab.click('button[type="submit"]');
  await tab.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });

  const shot = async (page, name) => {
    if (shots) {
      mkdirSync(shots, { recursive: true });
      await page.screenshot({ path: join(shots, `${name}.png`), fullPage: true });
    }
  };

  // ---- The kiosk into the pilot channel, on the releases page ----
  await tab.goto(`${base}/releases`, { waitUntil: 'networkidle' });
  await tab.locator('[role="tab"]').nth(2).click();
  const channelCell = tab.locator(`[data-kiosk="${kiosk.id}"]`).locator('xpath=..').locator('mat-select');
  await channelCell.click();
  await tab.locator('mat-option', { hasText: 'Pilot' }).click();
  await tab.waitForTimeout(1000);
  check((await call('GET', '/api/admin/kiosks/versions')).find((item) => item.kioskId === kiosk.id)?.channel === 'Pilot', `${code} is in the pilot channel`);

  /** Upload a package in the dialog and roll it out to the pilot channel; the release's id. */
  const uploadAndRollOut = async (dir, version) => {
    await tab.goto(`${base}/releases`, { waitUntil: 'networkidle' });
    const existing = () => call('GET', '/api/admin/releases').then((page) => page.releases.find((item) => item.version === version && item.component === 'App'));

    // A run cut short may have uploaded it already: a version is taken once.
    if (!(await existing())) {
      await tab.click('[data-action="upload"]');
      const chosen = files(dir);
      for (const slot of ['package', 'manifest', 'signature']) {
        await tab.locator(`[data-file="${slot}"] input[type="file"]`).setInputFiles(chosen[slot]);
      }
      await tab.click('[data-action="upload-confirm"]');
      await tab.waitForSelector('[data-action="upload-confirm"]', { state: 'detached', timeout: 60_000 });
    }

    const release = await existing();
    check(release?.channel === 'Pilot', `${version} uploaded through the panel, in the pilot channel`);

    // The page reads itself again after the upload (ReleasesChanged): let it settle before the menu.
    await tab.waitForSelector(`[data-release="${release.id}"]`);
    await tab.waitForTimeout(2500);
    await tab.locator(`[data-release="${release.id}"]`).locator('xpath=..').locator('button[mat-icon-button]').click();
    await tab.click('[data-action="rollout"]', { timeout: 10_000 });
    await tab.click('[data-target="pilot"] input');
    await tab.click('[data-action="rollout-confirm"]');
    await tab.waitForSelector('[data-action="rollout-confirm"]', { state: 'detached', timeout: 30_000 });
    check((await call('GET', '/api/admin/releases')).rollouts.some((rollout) => rollout.releaseId === release.id && rollout.status === 'Active'),
      `${version} rolled out to the pilot channel through the panel`);
    return release.id;
  };

  /** What the kiosk runs, as its tab says - the running column of the kiosk application's row. */
  const running = async () => (await tab.locator('[data-table="versions"] tr.mat-mdc-row').first().locator('td').nth(1).textContent())?.trim();

  /** The kiosk's update tab, watched until a component reaches a status; what it showed on the way. */
  const follow = async (version, until, minutes) => {
    await tab.goto(`${base}/kiosks/${kiosk.id}`, { waitUntil: 'networkidle' });
    await tab.locator('[role="tab"]', { hasText: 'Yeniləmə' }).click();
    await tab.waitForSelector('[data-table="versions"]');
    const seen = [];
    let picturedWaiting = false;
    const deadline = Date.now() + minutes * 60_000;
    while (Date.now() < deadline) {
      const row = (await tab.locator('[data-table="versions"] tr.mat-mdc-row').first().textContent()) ?? '';
      const status = row.match(/(Endirilir|Hazırdır, gözləyir|Boş an gözlənilir|Yeni müştəri qəbul edilmir|Yenilənir|Yoxlanılır|Quraşdırılıb|Alınmadı|Geri qaytarılıb) · (\S+)/);
      // The message follows the chip with no space between: the version is where the text starts.
      const ours = status && status[2].startsWith(version);
      if (ours && seen.at(-1) !== status[1]) {
        seen.push(status[1]);
        if (!picturedWaiting && (status[1] === 'Boş an gözlənilir' || status[1] === 'Yenilənir')) {
          picturedWaiting = true;
          await shot(tab, `kiosk-update-${until === 'Quraşdırılıb' ? 'during' : 'broken-during'}`);
        }
      }

      if (ours && status[1] === until) {
        return seen;
      }

      await tab.waitForTimeout(1000);
    }

    return seen;
  };

  // ---- A new version: uploaded, rolled out, installed at the idle moment ----
  await uploadAndRollOut(goodDir, good);
  const goodSeen = await follow(good, 'Quraşdırılıb', 8);
  check(goodSeen.at(-1) === 'Quraşdırılıb', `the tab followed ${good} live: ${goodSeen.join(' → ')}`);
  await tab.waitForTimeout(3000);
  check((await running()) === good, `${code} runs ${good}: ${await running()}`);
  await shot(tab, 'kiosk-update-installed');

  const devicesAfter = await call('GET', '/api/admin/devices');
  const deviceAfter = devicesAfter.find((device) => device.kioskId === kiosk.id && device.status === 'Active')?.id;
  check(deviceBefore && deviceAfter === deviceBefore && devicesAfter.length === devicesBefore.length,
    `no new registration: the same device (${deviceBefore?.slice(0, 8)}) before and after, ${devicesAfter.length} devices as before`);
  const status = (await call('GET', '/api/admin/kiosks')).find((item) => item.id === kiosk.id)?.status;
  check(['Online', 'NotConfigured'].includes(status), `${code} goes on working: ${status}`);

  // ---- A version that cannot start: rolled back by itself, with an incident ----
  const incidentsBefore = (await call('GET', `/api/admin/incidents?page=1&pageSize=100&type=UpdateFailed`)).items.filter((item) => item.kioskId === kiosk.id).length;
  await uploadAndRollOut(brokenDir, broken);
  const brokenSeen = await follow(broken, 'Geri qaytarılıb', 10);
  check(brokenSeen.at(-1) === 'Geri qaytarılıb', `the tab followed ${broken} live: ${brokenSeen.join(' → ')}`);
  await tab.waitForSelector('[data-notice="rolledBack"]', { timeout: 30_000 });
  await tab.waitForTimeout(2000);
  check((await running()) === good, `${code} is back on ${good}: ${await running()}`);
  await shot(tab, 'kiosk-update-rolled-back');

  const incidents = (await call('GET', `/api/admin/incidents?page=1&pageSize=100&type=UpdateFailed`)).items.filter((item) => item.kioskId === kiosk.id);
  check(incidents.length === incidentsBefore + 1 && incidents.some((item) => item.severity === 'High' && item.details.includes(broken)),
    `one High incident for ${broken}: ${incidents.find((item) => item.details.includes(broken))?.details.slice(0, 90)}…`);
  await tab.goto(`${base}/incidents`, { waitUntil: 'networkidle' });
  await tab.waitForTimeout(1500);
  check((await tab.textContent('body'))?.includes(broken), 'the panel\'s incident list shows it');
  await shot(tab, 'incidents');

  // ---- Logs: asked for with a reason, then downloaded ----
  await tab.goto(`${base}/kiosks/${kiosk.id}`, { waitUntil: 'networkidle' });
  await tab.locator('[role="tab"]', { hasText: 'Yeniləmə' }).click();
  await tab.waitForSelector('[data-command="upload-logs"]');
  const logsBefore = (await call('GET', `/api/admin/kiosks/${kiosk.id}/logs`)).length;
  await tab.click('[data-command="upload-logs"]');
  const confirm = tab.locator('mat-dialog-actions button[mat-flat-button]');
  check(await confirm.isDisabled(), 'the command cannot be sent without a reason');
  await tab.fill('mat-dialog-content textarea', 'ok');
  check(await confirm.isDisabled(), '... nor with a two-letter one');
  await tab.fill('mat-dialog-content textarea', 'M18 sınağı: rollback-dan sonra loglar');
  await confirm.click();
  await tab.waitForFunction((count) => document.querySelectorAll('[data-table="logs"] tr.mat-mdc-row').length > count, logsBefore, { timeout: 120_000 });
  check(true, 'the uploaded log appears in the tab by itself');
  const commands = await call('GET', `/api/admin/kiosks/${kiosk.id}/commands`);
  check(commands.find((command) => command.type === 'UploadLogs')?.reason === 'M18 sınağı: rollback-dan sonra loglar', 'the command carries its reason');
  const [download] = await Promise.all([tab.waitForEvent('download'), tab.locator('[data-download]').first().click()]);
  check(download.suggestedFilename().startsWith(code) && download.suggestedFilename().endsWith('.log.gz'), `the log downloads: ${download.suggestedFilename()}`);
  await shot(tab, 'kiosk-update-logs');

  // ---- The releases page: the rollouts' progress ----
  await tab.goto(`${base}/releases`, { waitUntil: 'networkidle' });
  await tab.locator('[role="tab"]').nth(1).click();
  await tab.waitForSelector('[data-progress]');
  await shot(tab, 'releases-progress');

  // ---- Phone width ----
  const phone = await browser.newContext({ viewport: { width: 390, height: 844 }, serviceWorkers: 'block', storageState: await context.storageState() });
  const small = await phone.newPage();
  watch(small, 'phone');
  await small.goto(`${base}/kiosks/${kiosk.id}`, { waitUntil: 'networkidle' });
  await small.locator('[role="tab"]', { hasText: 'Yeniləmə' }).click();
  await small.waitForSelector('[data-table="versions"]');
  const overflow = await small.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  check(overflow <= 0, `no sideways scroll of the page at phone width (${overflow}px)`);
  await shot(small, 'kiosk-update-phone');
  await small.goto(`${base}/releases`, { waitUntil: 'networkidle' });
  await small.locator('[role="tab"]').nth(1).click();
  await small.waitForSelector('[data-progress]');
  await shot(small, 'releases-progress-phone');

  check(consoleErrors.length === 0, `no console error (${consoleErrors.length})${consoleErrors.length ? ': ' + consoleErrors.slice(0, 3).join(' | ') : ''}`);
} finally {
  await browser.close();
  await setting('Kiosk.UpdateWindow', window ?? '02:00-05:00');
  await setting('Update.HealthTimeoutMinutes', health ?? 5);
  console.log(`       settings put back: Kiosk.UpdateWindow ${window}, Update.HealthTimeoutMinutes ${health}`);
}

console.log(failures.length === 0 ? '\nAll checks passed.' : `\n${failures.length} check(s) failed.`);
process.exit(failures.length === 0 ? 0 : 1);
