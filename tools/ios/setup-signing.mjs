#!/usr/bin/env node
// NeoPrint - signing for the iOS TestFlight build (T-469, docs/IOS_TESTING.md).
//
// Run once from Git Bash, after the App Store Connect API key exists:
//
//   node tools/ios/setup-signing.mjs --key ~/Downloads/AuthKey_ABC123.p8 \
//     --key-id ABC123 --issuer-id 1234abcd-...            # check the key only
//   node tools/ios/setup-signing.mjs ... --apply          # do it
//
// With --apply, through the App Store Connect API:
//   1. the bundle ID (az.neoprint.neoprint) is registered if it is not, and
//      Push Notifications is turned on for it;
//   2. a new private key and certificate request are made here (openssl), and
//      Apple issues an "Apple Distribution" certificate for it;
//   3. an App Store provisioning profile for the bundle ID and certificate;
//   4. everything the "Deploy (test server)" workflow needs goes into the
//      repository's GitHub secrets (gh secret set, through stdin).
//
// Nothing secret is printed or written into the repository; the private key
// and the .p12 live in a temporary folder that is removed at the end. Run it
// again a year on, when the certificate and profile expire. Apple allows only
// a few distribution certificates: an old one is revoked at
// developer.apple.com → Certificates.
//
// Our own code, no package: node's crypto signs the ES256 token, openssl
// (it comes with Git for Windows) makes the request and the .p12.

import { spawnSync } from 'node:child_process';
import { createPrivateKey, randomBytes, sign } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { join } from 'node:path';

const API = 'https://api.appstoreconnect.apple.com';

function args() {
  const out = { bundleId: 'az.neoprint.neoprint', apply: false };
  const argv = process.argv.slice(2);
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--apply') out.apply = true;
    else if (a === '--key') out.key = argv[++i];
    else if (a === '--key-id') out.keyId = argv[++i];
    else if (a === '--issuer-id') out.issuerId = argv[++i];
    else if (a === '--bundle-id') out.bundleId = argv[++i];
    else fail(`Unknown argument: ${a}`);
  }
  if (!out.key || !out.keyId || !out.issuerId) {
    fail('Usage: node tools/ios/setup-signing.mjs --key <AuthKey_X.p8> --key-id <X> --issuer-id <uuid> [--bundle-id <id>] [--apply]');
  }
  if (out.key.startsWith('~')) out.key = join(homedir(), out.key.slice(1));
  if (!/^[A-Z0-9]{8,12}$/.test(out.keyId)) fail('--key-id looks wrong (10 letters and digits, from App Store Connect).');
  if (!/^[0-9a-f-]{36}$/i.test(out.issuerId)) fail('--issuer-id looks wrong (a UUID, above the keys list).');
  if (!/^[A-Za-z0-9.-]+$/.test(out.bundleId)) fail('--bundle-id looks wrong.');
  return out;
}

function fail(message) {
  console.error(`✗ ${message}`);
  process.exit(1);
}

const b64url = (data) => Buffer.from(data).toString('base64url');

function token(opts, keyPem) {
  const now = Math.floor(Date.now() / 1000);
  const head = b64url(JSON.stringify({ alg: 'ES256', kid: opts.keyId, typ: 'JWT' }));
  const body = b64url(JSON.stringify({ iss: opts.issuerId, iat: now, exp: now + 15 * 60, aud: 'appstoreconnect-v1' }));
  const signature = sign('sha256', Buffer.from(`${head}.${body}`), {
    key: createPrivateKey(keyPem),
    dsaEncoding: 'ieee-p1363',
  });
  return `${head}.${body}.${b64url(signature)}`;
}

function client(jwt) {
  return async (method, path, body) => {
    const res = await fetch(API + path, {
      method,
      headers: { Authorization: `Bearer ${jwt}`, 'Content-Type': 'application/json' },
      body: body ? JSON.stringify(body) : undefined,
    });
    const text = await res.text();
    const json = text ? JSON.parse(text) : {};
    if (!res.ok) {
      const detail = (json.errors ?? []).map((e) => `${e.title}: ${e.detail}`).join('; ');
      const err = new Error(`${method} ${path} → ${res.status} ${detail}`);
      err.status = res.status;
      throw err;
    }
    return json;
  };
}

function openssl(argv, env = {}) {
  // Git Bash would otherwise turn "/CN=..." into a Windows path.
  const r = spawnSync('openssl', argv, {
    encoding: 'utf8',
    env: { ...process.env, MSYS2_ARG_CONV_EXCL: '*', ...env },
  });
  if (r.error) fail('openssl not found: run this from Git Bash (Git for Windows carries it).');
  if (r.status !== 0) fail(`openssl ${argv[0]} failed: ${r.stderr.trim()}`);
  return r.stdout;
}

function secret(name, value) {
  const r = spawnSync('gh', ['secret', 'set', name], { input: value, encoding: 'utf8' });
  if (r.error) fail('gh not found: install the GitHub CLI and run "gh auth login".');
  if (r.status !== 0) fail(`gh secret set ${name} failed: ${r.stderr.trim()}`);
  console.log(`  ✓ secret ${name}`);
}

async function main() {
  const opts = args();
  let keyPem;
  try {
    keyPem = readFileSync(opts.key, 'utf8');
  } catch {
    fail(`Cannot read the key file: ${opts.key}`);
  }

  const api = client(token(opts, keyPem));

  // The key works and can see the bundle IDs.
  const found = await api('GET', `/v1/bundleIds?filter[identifier]=${encodeURIComponent(opts.bundleId)}&limit=200`)
    .catch((e) => fail(`The API key does not work: ${e.message}`));
  let bundle = found.data.find((b) => b.attributes.identifier === opts.bundleId);
  console.log(`✓ The API key works. Bundle ID ${opts.bundleId}: ${bundle ? 'registered' : 'not registered yet'}.`);

  if (!opts.apply) {
    console.log('Nothing changed. Run again with --apply to set up the signing.');
    return;
  }

  if (!bundle) {
    bundle = (await api('POST', '/v1/bundleIds', {
      data: { type: 'bundleIds', attributes: { identifier: opts.bundleId, name: 'NeoPrint', platform: 'IOS' } },
    })).data;
    console.log('  ✓ bundle ID registered');
  }

  try {
    await api('POST', '/v1/bundleIdCapabilities', {
      data: {
        type: 'bundleIdCapabilities',
        attributes: { capabilityType: 'PUSH_NOTIFICATIONS' },
        relationships: { bundleId: { data: { type: 'bundleIds', id: bundle.id } } },
      },
    });
    console.log('  ✓ Push Notifications on');
  } catch (e) {
    if (e.status !== 409) throw e;
    console.log('  ✓ Push Notifications already on');
  }

  const dir = mkdtempSync(join(tmpdir(), 'neoprint-ios-'));
  try {
    const keyFile = join(dir, 'key.pem');
    const csrFile = join(dir, 'request.csr');
    openssl(['req', '-new', '-newkey', 'rsa:2048', '-nodes', '-keyout', keyFile, '-out', csrFile, '-subj', '/CN=NeoPrint CI/O=NeoPrint']);

    let cert;
    try {
      cert = (await api('POST', '/v1/certificates', {
        data: { type: 'certificates', attributes: { certificateType: 'DISTRIBUTION', csrContent: readFileSync(csrFile, 'utf8') } },
      })).data;
    } catch (e) {
      if (e.status === 409) {
        fail('Apple has no room for another distribution certificate. Revoke an old one at developer.apple.com → Certificates, then run again.');
      }
      throw e;
    }
    console.log(`  ✓ Apple Distribution certificate, valid until ${cert.attributes.expirationDate.slice(0, 10)}`);

    const derFile = join(dir, 'cert.der');
    const pemFile = join(dir, 'cert.pem');
    const p12File = join(dir, 'dist.p12');
    writeFileSync(derFile, Buffer.from(cert.attributes.certificateContent, 'base64'));
    openssl(['x509', '-inform', 'DER', '-in', derFile, '-out', pemFile]);
    const password = randomBytes(24).toString('base64url');
    // 3DES and SHA-1: what the macOS keychain imports (OpenSSL 3's default it does not).
    openssl(['pkcs12', '-export', '-inkey', keyFile, '-in', pemFile, '-out', p12File,
      '-certpbe', 'PBE-SHA1-3DES', '-keypbe', 'PBE-SHA1-3DES', '-macalg', 'sha1',
      '-passout', 'env:P12_PASSWORD'], { P12_PASSWORD: password });

    const stamp = new Date().toISOString().slice(0, 10);
    const profile = (await api('POST', '/v1/profiles', {
      data: {
        type: 'profiles',
        attributes: { name: `NeoPrint TestFlight ${stamp} ${cert.id.slice(0, 6)}`, profileType: 'IOS_APP_STORE' },
        relationships: {
          bundleId: { data: { type: 'bundleIds', id: bundle.id } },
          certificates: { data: [{ type: 'certificates', id: cert.id }] },
        },
      },
    })).data;
    console.log(`  ✓ App Store profile "${profile.attributes.name}"`);

    const profileBytes = Buffer.from(profile.attributes.profileContent, 'base64');
    const team = /<key>TeamIdentifier<\/key>\s*<array>\s*<string>([A-Z0-9]+)<\/string>/.exec(profileBytes.toString('latin1'));
    if (!team) fail('The profile names no team.');

    console.log('GitHub secrets:');
    secret('IOS_DIST_P12_B64', readFileSync(p12File).toString('base64'));
    secret('IOS_DIST_P12_PASSWORD', password);
    secret('IOS_PROFILE_B64', profileBytes.toString('base64'));
    secret('ASC_KEY_P8_B64', Buffer.from(keyPem).toString('base64'));
    secret('ASC_KEY_ID', opts.keyId);
    secret('ASC_ISSUER_ID', opts.issuerId);
    console.log(`✓ Done (team ${team[1]}). The workflow: Actions → "Deploy (test server)" → ios.`);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

main().catch((e) => fail(e.message));
