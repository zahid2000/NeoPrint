// ---------------------------------------------------------------------------
// Releases and rollouts on the development stack (step 7B, M16 - T-317): a
// package built here, signed with the staging release key the way the owner
// signs one, uploaded to the panel's API; a changed package and a package of
// the other key set refused; a rollout to one of two kiosks - the one it
// reaches is offered the very manifest and signature that were signed and
// downloads the package byte for byte, which tools/release-sign verifies as a
// staging kiosk would; the other kiosk is offered nothing and cannot download
// it. The kiosk reports its update and the panel shows it. Pictures of the
// "Buraxılışlar" page's three tabs.
//
//   docker compose up -d --build api admin
//   dotnet build tools/release-sign                        # from the repository
//   node e2e/releases.mjs                                  # from admin/
//   node e2e/releases.mjs ../docs/screenshots/step-7b/m16
//
// Needs the staging release key on this machine (docs/RELEASE_SIGNING.md §0,
// ~/.neoprint/release-keys/staging) and Chrome or Edge for the pictures.
// Repeatable: every run builds a package of its own version; the kiosks, the
// release and its rollouts go at the end (the audit log keeps what happened).
// ---------------------------------------------------------------------------
import { execFileSync } from 'node:child_process';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

import { kioskSession, makeKiosk, purgeKiosk } from './dev-data.mjs';
import { launchBrowser } from './pages.mjs';

const base = process.env.ADMIN_URL ?? 'http://localhost:8080';
const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
// The development seed's password (docs/DEV_ENVIRONMENT.md section 4); never a real one.
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const shots = process.argv[2] ? resolve(process.argv[2]) : null;
const repo = resolve(new URL('../..', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
const tool = join(repo, 'tools', 'release-sign', 'bin', 'Debug', 'net8.0', 'release-sign.dll');

const failures = [];
const check = (ok, text) => {
  console.log(`${ok ? 'ok    ' : 'WRONG '} ${text}`);
  if (!ok) failures.push(text);
};

const login = await fetch(`${api}/api/auth/admin/login`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: 'dev-superadmin@neoprint.local', password }),
});
if (!login.ok) throw new Error(`sign-in: ${login.status}`);
const root = (await login.json()).accessToken;

const call = async (method, path, body, anonymous = false) => {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { ...(anonymous ? {} : { Authorization: `Bearer ${root}` }), 'Content-Type': 'application/json', 'Idempotency-Key': randomUUID() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${(await response.text()).slice(0, 200)}`);
  return response.status === 204 ? null : response.json();
};

const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');
const releaseSign = (...args) => execFileSync('dotnet', [tool, ...args], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });

async function upload(dir, file, manifest = `${file}.manifest.json`, signature = `${manifest}.sig`, sentAs = file) {
  const form = new FormData();
  form.append('package', new Blob([readFileSync(join(dir, file))]), sentAs);
  form.append('manifest', new Blob([readFileSync(join(dir, manifest))]), manifest);
  form.append('signature', new Blob([readFileSync(join(dir, signature))]), signature);
  form.append('channel', 'Pilot');
  const response = await fetch(`${api}/api/admin/releases`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${root}`, 'Idempotency-Key': randomUUID() },
    body: form,
  });
  return { status: response.status, body: await response.json() };
}

const work = mkdtempSync(join(tmpdir(), 'neoprint-release-'));
const made = [];
let releaseId = null;

try {
  // ---- The server's key set ----
  const page = await call('GET', '/api/admin/releases');
  check(page.keySet === 'staging' && page.trustedKeys === 2, `the development stack takes staging packages (${page.keySet}, ${page.trustedKeys} keys)`);

  // ---- A package of this run's own version, signed with the staging key ----
  const seconds = Math.floor(Date.now() / 1000);
  const version = `0.${Math.floor(seconds / 1_000_000)}.${seconds % 1_000_000}`;
  const file = `neoprint-kiosk-agent-${version}-linux-x64-staging.tar.gz`;
  writeFileSync(join(work, file), randomBytes(64 * 1024));
  releaseSign('manifest', '--package', join(work, file), '--component', 'Agent', '--platform', 'linux-x64', '--key-set', 'staging',
    '--version', version, '--min-agent-version', '0.0.1', '--out', join(work, `${file}.manifest.json`));
  releaseSign('sign-staging', '--manifest', join(work, `${file}.manifest.json`), '--package', join(work, file));

  // ---- Refused: a changed package, a package of the other key set ----
  const changed = `changed-${file}`;
  const bytes = readFileSync(join(work, file));
  bytes[100] ^= 1;
  writeFileSync(join(work, changed), bytes);
  writeFileSync(join(work, `${changed}.manifest.json`), readFileSync(join(work, `${file}.manifest.json`)));
  writeFileSync(join(work, `${changed}.manifest.json.sig`), readFileSync(join(work, `${file}.manifest.json.sig`)));
  // Under the package's own name: only its bytes differ.
  const refusedChanged = await upload(work, changed, `${changed}.manifest.json`, `${changed}.manifest.json.sig`, file);
  check(refusedChanged.status === 400, `a changed package is refused (${refusedChanged.status}): ${JSON.stringify(refusedChanged.body.errors ?? {}).slice(0, 160)}`);

  const production = file.replace('-staging', '-production');
  writeFileSync(join(work, production), randomBytes(1024));
  releaseSign('manifest', '--package', join(work, production), '--component', 'Agent', '--platform', 'linux-x64', '--key-set', 'production',
    '--version', `${version}-p`, '--min-agent-version', '0.0.1', '--out', join(work, `${production}.manifest.json`));
  writeFileSync(join(work, `${production}.manifest.json.sig`), readFileSync(join(work, `${file}.manifest.json.sig`)));
  const refusedProduction = await upload(work, production);
  check(refusedProduction.status === 400, `a production package with a staging signature is refused (${refusedProduction.status})`);

  // ---- Taken ----
  const uploaded = await upload(work, file);
  check(uploaded.status === 200 && uploaded.body.keySet === 'staging', `the signed package is taken: Agent ${uploaded.body.version}, key ${uploaded.body.keyId}`);
  releaseId = uploaded.body.id;

  // ---- Two kiosks, a rollout to one ----
  const reached = await makeKiosk(call, 'E2E buraxılış A');
  const left = await makeKiosk(call, 'E2E buraxılış B');
  made.push(reached, left);
  const asReached = await kioskSession(api, reached.deviceId, reached.privateKey);
  const asLeft = await kioskSession(api, left.deviceId, left.privateKey);

  const pilotToAll = await fetch(`${api}/api/admin/rollouts`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${root}`, 'Content-Type': 'application/json', 'Idempotency-Key': randomUUID() },
    body: JSON.stringify({ releaseId, target: 'All' }),
  });
  check(pilotToAll.status === 409, `a pilot release does not go to everyone (${pilotToAll.status})`);

  const rollout = await call('POST', '/api/admin/rollouts', { releaseId, target: 'Kiosks', kioskIds: [reached.id] });
  check(rollout.reaches === 1, `the rollout reaches ${rollout.reaches} kiosk`);

  const offer = (await asReached('GET', '/api/kiosks/me/update')).offers.find((o) => o.releaseId === releaseId);
  check(offer?.manifest === readFileSync(join(work, `${file}.manifest.json`), 'utf8'), 'the kiosk it reaches is offered the very manifest that was signed');
  check(offer?.signature === readFileSync(join(work, `${file}.manifest.json.sig`), 'utf8'), '... and its very signature');

  const download = await fetch(`${api}/${offer.download}`, { headers: { Authorization: `Bearer ${asReached.token}` } });
  const got = Buffer.from(await download.arrayBuffer());
  check(download.ok && sha256(got) === sha256(readFileSync(join(work, file))), `the kiosk downloads the package byte for byte (${got.length} bytes)`);

  // As a staging kiosk checks it, with the tool that shares its code.
  mkdirSync(join(work, 'kiosk'));
  writeFileSync(join(work, 'kiosk', 'manifest.json'), offer.manifest);
  writeFileSync(join(work, 'kiosk', 'manifest.json.sig'), offer.signature);
  writeFileSync(join(work, 'kiosk', file), got);
  const verified = releaseSign('verify', '--key-set', 'staging', '--manifest', join(work, 'kiosk', 'manifest.json'), '--package', join(work, 'kiosk', file));
  check(verified.includes('OK:'), `release-sign verify as a staging kiosk: ${verified.trim().split('\n').pop()}`);
  let productionKiosk = '';
  try {
    releaseSign('verify', '--key-set', 'production', '--manifest', join(work, 'kiosk', 'manifest.json'), '--package', join(work, 'kiosk', file));
  } catch (error) {
    productionKiosk = String(error.stderr);
  }
  check(productionKiosk.includes('UnknownKey'), `... and a production kiosk refuses it: ${productionKiosk.trim().split('\n').pop()}`);

  const leftOffers = (await asLeft('GET', '/api/kiosks/me/update')).offers;
  check(!leftOffers.some((o) => o.releaseId === releaseId), 'the kiosk it does not reach is offered nothing of it');
  const leftDownload = await fetch(`${api}/api/releases/${releaseId}/download`, { headers: { Authorization: `Bearer ${asLeft.token}` } });
  check(leftDownload.status === 404, `... and cannot download it (${leftDownload.status})`);

  // ---- The kiosk reports, the panel shows ----
  await asReached('POST', '/api/kiosks/me/update-status', { component: 'Agent', version, status: 'Ready' });
  const row = (await call('GET', '/api/admin/kiosks/versions')).find((k) => k.kioskId === reached.id);
  check(row?.updates?.[0]?.status === 'Ready' && row?.offered?.some((o) => o.version === version), `the panel shows the kiosk's update: ${row?.updates?.[0]?.status}, offered ${version}`);

  // ---- Pictures ----
  if (shots) {
    mkdirSync(shots, { recursive: true });
    const browser = await launchBrowser();
    const tab = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    await tab.goto(`${base}/login`, { waitUntil: 'networkidle' });
    await tab.fill('input[type="email"]', 'dev-superadmin@neoprint.local');
    await tab.fill('input[type="password"]', password);
    await tab.click('button[type="submit"]');
    await tab.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20_000 });
    await tab.goto(`${base}/releases`, { waitUntil: 'networkidle' });
    await tab.waitForSelector('[data-tab="releases"]');
    await tab.screenshot({ path: join(shots, 'releases.png'), fullPage: true });
    for (const [index, name] of [[1, 'rollouts'], [2, 'kiosks']]) {
      await tab.locator('[role="tab"]').nth(index).click();
      await tab.waitForSelector(`[data-tab="${name}"]`);
      await tab.screenshot({ path: join(shots, `releases-${name}.png`), fullPage: true });
    }
    await browser.close();
    console.log(`       pictures in ${shots}`);
  }

  // ---- Withdrawn: offered to nobody, named as not to be installed ----
  await call('POST', `/api/admin/releases/${releaseId}/withdraw`, { reason: 'e2e: geri çəkmə yoxlanılır' });
  const after = await asReached('GET', '/api/kiosks/me/update');
  check(!after.offers.some((o) => o.releaseId === releaseId) && after.withdrawn.includes(uploaded.body.sha256), 'a withdrawn package is offered to nobody and named as not to be installed');
} finally {
  for (const kiosk of made) {
    purgeKiosk(api, kiosk.id, [kiosk.deviceId]);
  }

  if (releaseId) {
    // The run's own release and rollouts; the stored package is the file cleanup's (an object nothing points at).
    execFileSync('docker', ['exec', 'neoprint-mongo', 'mongosh', '-u', 'neoprint', '-p', 'devMongoPassword', '--authenticationDatabase', 'admin', 'neoprint', '--quiet', '--eval',
      `db.release_rollouts.deleteMany({ ReleaseId: UUID("${releaseId}") }); db.releases.deleteOne({ _id: UUID("${releaseId}") });`],
    { env: { ...process.env, MSYS_NO_PATHCONV: '1' } });
  }

  rmSync(work, { recursive: true, force: true });
}

console.log(failures.length === 0 ? '\nAll checks passed.' : `\n${failures.length} check(s) failed.`);
process.exit(failures.length === 0 ? 0 : 1);
