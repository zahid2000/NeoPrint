// ---------------------------------------------------------------------------
// NeoPrint dev-kiosk: plays a UI that cannot be trusted (T-321). It writes
// requests straight into the container kiosk's requests folder - past the
// UI's own checks - and shows what the agent answers. Every case must come
// back Refused and leave the running version as it was (docs/KIOSK_UPDATES.md).
//
//   bash tools/dev-kiosk/make-release.sh app 0.8.0-m17.54        # newer than the running one, NOT uploaded
//   node tools/dev-kiosk/agent-forge.mjs /tmp/neoprint-releases/app-0.8.0-m17.54 [older-release-dir]
//
// CONTAINER (default neoprint-kupd) names the kiosk. Cases: a package with one
// byte changed; the manifest signed by another key under the trusted key id;
// the manifest rewritten to the changed package's hash; an unknown key id; a
// withdrawn package; a package name that leaves the downloads folder; and,
// with an older release folder, a version that is not newer.
// ---------------------------------------------------------------------------
import { createHash, generateKeyPairSync, randomUUID, sign } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const container = process.env.CONTAINER ?? 'neoprint-kupd';
const [releaseDir, olderDir] = process.argv.slice(2);
if (!releaseDir) {
  console.error('usage: node tools/dev-kiosk/agent-forge.mjs <newer-release-dir> [older-release-dir]');
  process.exit(2);
}

const docker = (...args) => execFileSync('docker', args, {
  encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, MSYS_NO_PATHCONV: '1' },
});
const work = mkdtempSync(join(tmpdir(), 'neoprint-forge-'));

function release(dir) {
  const files = readdirSync(dir);
  const manifestName = files.find((f) => f.endsWith('.manifest.json'));
  return {
    manifest: readFileSync(join(dir, manifestName), 'utf8'),
    signature: readFileSync(join(dir, `${manifestName}.sig`), 'utf8'),
    package: readFileSync(join(dir, manifestName.replace(/\.manifest\.json$/, ''))),
  };
}

const good = release(releaseDir);
const claimed = JSON.parse(good.manifest);
const signatureFile = JSON.parse(good.signature);

const tampered = Buffer.from(good.package);
tampered[Math.floor(tampered.length / 2)] ^= 0xff;

// A key that is not the kiosk's, signing as if it were.
const { privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
const signWith = (text, keyId) => JSON.stringify({
  keyId,
  algorithm: 'ES256',
  signature: sign('sha256', Buffer.from(text, 'utf8'), { key: privateKey, dsaEncoding: 'ieee-p1363' }).toString('base64'),
});
const rewritten = good.manifest.replace(claimed.sha256, createHash('sha256').update(tampered).digest('hex'));

const cases = [
  { name: 'one byte of the package changed', package: tampered, manifest: good.manifest, signature: good.signature },
  { name: 'manifest signed by another key, trusted key id', package: good.package, manifest: good.manifest, signature: signWith(good.manifest, signatureFile.keyId) },
  { name: 'manifest rewritten to the changed package', package: tampered, manifest: rewritten, signature: good.signature },
  { name: 'manifest rewritten and signed by another key', package: tampered, manifest: rewritten, signature: signWith(rewritten, signatureFile.keyId) },
  { name: 'unknown key id', package: good.package, manifest: good.manifest, signature: signWith(good.manifest, '0000000000000000') },
  { name: 'withdrawn package', package: good.package, manifest: good.manifest, signature: good.signature, withdrawn: [claimed.sha256] },
  { name: 'package name outside the downloads folder', packageFile: '../../agent/state.json', manifest: good.manifest, signature: good.signature },
];
if (olderDir) {
  const older = release(olderDir);
  cases.push({ name: 'not newer than the running version', package: older.package, manifest: older.manifest, signature: older.signature });
}

const before = docker('exec', container, 'readlink', '/opt/neoprint/app/current').trim();
console.log(`${container}: app ${before}; offering ${claimed.component} ${claimed.version} ${cases.length} ways.`);

for (const c of cases) {
  const id = randomUUID();
  const packageFile = c.packageFile ?? `${id.replaceAll('-', '')}.pkg`;
  if (c.package) {
    const local = join(work, packageFile);
    writeFileSync(local, c.package);
    docker('cp', local, `${container}:/var/lib/neoprint/updates/downloads/${packageFile}`);
  }

  const request = {
    id, kind: 'Install', createdAt: new Date().toISOString(), releaseId: randomUUID(), packageFile,
    manifest: c.manifest, signature: c.signature, withdrawn: c.withdrawn ?? [], healthTimeoutSeconds: 60, keepVersions: 3, retry: true,
  };
  const local = join(work, `${id}.json`);
  writeFileSync(local, JSON.stringify(request));
  docker('cp', local, `${container}:/var/lib/neoprint/updates/requests/${id.replaceAll('-', '')}.json`);

  let answer = null;
  for (let i = 0; i < 60 && !answer; i++) {
    await new Promise((r) => setTimeout(r, 1000));
    try {
      answer = JSON.parse(docker('exec', container, 'cat', `/var/lib/neoprint/agent/results/${id.replaceAll('-', '')}.json`));
    } catch {
      // not yet
    }
  }

  const outcome = answer ? `${answer.outcome} ${answer.messageKey ?? ''}` : 'no answer in 60 s';
  console.log(`${answer?.outcome === 'Refused' ? 'ok  ' : 'FAIL'}  ${c.name}: ${outcome}`);
}

const after = docker('exec', container, 'readlink', '/opt/neoprint/app/current').trim();
const versions = docker('exec', container, 'ls', '/opt/neoprint/app/versions').trim().split(/\s+/);
console.log(`app ${after} (${after === before ? 'unchanged' : 'CHANGED'}); ${claimed.version} ${versions.some((v) => v === claimed.version) ? 'WAS UNPACKED' : 'never unpacked'}.`);
