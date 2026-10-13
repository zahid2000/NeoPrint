// ---------------------------------------------------------------------------
// The panel hears everything the server says over /hubs/admin (T-252).
//
//   node scripts/hub-check.mjs
//
// An event nobody listens to fails nothing: the backend sends it, the hub
// delivers it, and the page that should have followed shows what it loaded
// when it was opened (T-248 - the kiosk list stayed "Online" for a kiosk that
// had been offline for an hour). This check fails instead. It holds
// src/app/core/realtime/hub-events.ts against the backend's sources:
//
//   1. Every event the backend sends to the admin hub is in HubEvents, and
//      HubEvents names nothing the backend does not send.
//   2. Every event has a listener - `HubEvents.x` used somewhere in the panel
//      outside the list itself - or stands in `unheardHubEvents` with the
//      reason. One that has a listener may not stay in that list.
//   3. No event name is written as a string anywhere else in the panel: a
//      page subscribes with HubEvents.x, so the list stays the one place.
//
// Spec files are left out of 2 and 3: a test plays the server, and writes the
// name the way the server does. No dependencies, like scripts/i18n-check.mjs.
//
// What this cannot see is whether the backend ever calls the method that
// sends an event - four of them had no caller until T-261. That half is the
// backend's: HubContractTests reads the compiled code for a call to every
// method of the panel notifiers.
// ---------------------------------------------------------------------------
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

const root = new URL('..', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');
const app = join(root, 'src', 'app');
const backend = join(root, '..', 'backend', 'src');
const listFile = join(app, 'core', 'realtime', 'hub-events.ts');

function files(dir, extensions) {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);

    if (statSync(path).isDirectory()) {
      return name === 'bin' || name === 'obj' ? [] : files(path, extensions);
    }

    return extensions.some((extension) => name.endsWith(extension)) ? [path] : [];
  });
}

const problems = [];

// --- what the backend sends ------------------------------------------------
// Whatever holds an IHubContext<AdminHub>, and the hub class itself.
const sent = new Map();

for (const path of files(backend, ['.cs'])) {
  const source = readFileSync(path, 'utf8');

  if (!/IHubContext<AdminHub>|class AdminHub\b/.test(source)) {
    continue;
  }

  for (const call of source.matchAll(/\.SendAsync\(\s*([^,)\s]+)/g)) {
    const name = /^"([A-Za-z]+)"$/.exec(call[1]);

    if (!name) {
      problems.push(
        `${relative(root, path)}: SendAsync(${call[1]} …) - the event name is not written out, so it cannot be checked.`,
      );
      continue;
    }

    sent.set(name[1], relative(join(root, '..'), path));
  }
}

if (sent.size === 0) {
  problems.push('No event found in the backend sources - the check is looking in the wrong place.');
}

// --- what the panel declares -------------------------------------------------
const list = readFileSync(listFile, 'utf8');
const block = (start) => {
  const from = list.indexOf(start);

  if (from < 0) {
    return '';
  }

  // To the first line that closes the object literal.
  return list.slice(from, list.indexOf('\n}', from));
};

const declared = new Map(
  [...block('export const HubEvents = {').matchAll(/^\s*(\w+):\s*'(\w+)',?\s*$/gm)].map((entry) => [
    entry[1],
    entry[2],
  ]),
);

const unheard = new Map(
  [...block('export const unheardHubEvents').matchAll(/\[HubEvents\.(\w+)\]:\s*\n?\s*(['"`])((?:\\.|(?!\2).)*)\2/g)].map(
    (entry) => [entry[1], entry[3].trim()],
  ),
);

if (declared.size === 0) {
  problems.push('hub-events.ts: HubEvents could not be read.');
}

const names = new Set(declared.values());

for (const [name, where] of sent) {
  if (!names.has(name)) {
    problems.push(`${name}: sent by the backend (${where}) and missing from HubEvents.`);
  }
}

for (const [key, name] of declared) {
  if (!sent.has(name)) {
    problems.push(`HubEvents.${key} ('${name}'): the backend never sends it.`);
  }
}

for (const [key, reason] of unheard) {
  if (!declared.has(key)) {
    problems.push(`unheardHubEvents: HubEvents.${key} is not in the list.`);
  } else if (reason.length < 20) {
    problems.push(`unheardHubEvents: HubEvents.${key} needs a reason somebody can act on.`);
  }
}

// --- who listens -------------------------------------------------------------
const sources = files(app, ['.ts', '.html']).filter(
  (path) => path !== listFile && !path.endsWith('.spec.ts'),
);
const texts = new Map(sources.map((path) => [path, readFileSync(path, 'utf8')]));

for (const [key, name] of declared) {
  const use = new RegExp(`\\bHubEvents\\.${key}\\b`);
  const listeners = sources.filter((path) => use.test(texts.get(path)));

  if (listeners.length === 0 && !unheard.has(key)) {
    problems.push(
      `HubEvents.${key} ('${name}'): no page listens to it. Add a listener, or say why not in unheardHubEvents.`,
    );
  }

  if (listeners.length > 0 && unheard.has(key)) {
    problems.push(
      `HubEvents.${key}: listed in unheardHubEvents, yet ${relative(root, listeners[0])} listens to it - take the entry out.`,
    );
  }

  // In quotes, the way code names it; a comment that mentions it in backticks is not a subscription.
  const literal = new RegExp(`(['"])${name}\\1`);

  for (const path of sources) {
    if (literal.test(texts.get(path))) {
      problems.push(`${relative(root, path)}: '${name}' written as a string - use HubEvents.${key}.`);
    }
  }
}

if (problems.length > 0) {
  console.error(`hub-check: ${problems.length} problem(s)\n`);
  problems.forEach((problem) => console.error(`  ${problem}`));
  process.exit(1);
}

console.log(
  `hub-check: ${sent.size} events sent by the backend, ${declared.size} in HubEvents, ` +
    `${declared.size - unheard.size} with a listener, ${unheard.size} unheard with a reason.`,
);
