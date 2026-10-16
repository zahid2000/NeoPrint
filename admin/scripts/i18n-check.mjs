// ---------------------------------------------------------------------------
// No raw translation key in the panel (docs/DESIGN_SYSTEM.md section 6).
//
//   node scripts/i18n-check.mjs
//
// A key that has no translation is shown as the key itself -
// "legal.type.ScanNotice" in a table cell - and nothing fails: the template
// compiles, the unit tests pass. This check fails instead. It looks at the
// two ways a key reaches the screen:
//
//   1. Written out in a template or a component ('kiosks.title' | translate).
//      Every such key must be in the dictionaries.
//   2. Put together at run time from a value the server sends
//      ('jobs.status.' + job.status). Every such prefix must be listed in
//      SERVER_VALUES below with where its values come from - a backend enum,
//      the settings, the feature flags - and every one of those values must
//      have a translation. A new enum member or a new setting therefore
//      fails here until the panel can name it.
//
// The dictionaries themselves - the same keys in az, en and ru, the same
// placeholders, the right language - are checked by translations.spec.ts.
// No dependencies, like scripts/icon-subset.mjs.
// ---------------------------------------------------------------------------
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

const root = new URL('..', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');
const app = join(root, 'src', 'app');
const backend = join(root, '..', 'backend', 'src');

/**
 * Where the values behind each run-time prefix come from.
 *
 *   enum      a backend enum: every member needs `${prefix}${member}`
 *   except    members the panel is never sent, with the reason
 *   settings  the keys of AdminSettings.cs
 *   flags     the feature flags the backend seeds
 *   values    a closed list the panel computes itself
 *   suffixes  what follows the value in the key ('.name', '.description')
 */
const SERVER_VALUES = {
  'legal.type.': { enum: 'LegalDocumentType' },
  'customerDetail.consent.': { enum: 'LegalDocumentType' },
  'jobs.status.': { enum: 'PrintJobStatus' },
  'jobs.source.': { enum: 'PrintJobSource' },
  'jobs.actor.': { enum: 'ActorType' },
  'kiosks.status.': { enum: 'KioskStatus' },
  'money.status.': { enum: 'PaymentStatus' },
  'money.method.': { enum: 'PaymentMethod' },
  'money.purpose.': { enum: 'PaymentPurpose' },
  'money.channel.': { enum: 'PaymentChannel' },
  'money.refundStatus.': { enum: 'RefundStatus' },
  'money.refundReason.': { enum: 'RefundReason' },
  'money.destination.': { enum: 'RefundDestination' },
  'money.initiatedBy.': { enum: 'ActorType' },
  'money.fiscalType.': { enum: 'FiscalReceiptType' },
  'money.fiscalStatus.': { enum: 'FiscalReceiptStatus' },
  'money.walletType.': { enum: 'WalletTransactionType' },
  'money.customerStatus.': { enum: 'UserStatus' },
  'money.claimStatus.': { enum: 'ClaimStatus' },
  'money.claimChannel.': { enum: 'ClaimChannel' },
  'money.incidentSeverity.': { enum: 'IncidentSeverity' },
  'referrals.hold.': { enum: 'ReferralHold', except: { None: 'the reason is shown only when a reward is held' } },
  'referrals.outcome.': { values: ['Waiting', 'Rewarded', 'Held'] },
  'storage.health.': { enum: 'StorageHealthStatus' },
  'storage.role.': { enum: 'StorageProviderRole' },
  'storage.sync.status.': { enum: 'StorageSyncStatus' },
  'storage.sync.mode.': { enum: 'StorageSyncMode' },
  'storage.progress.stage.': { enum: 'StorageSyncStatus' },
  'printers.command.': { enum: 'KioskCommandType' },
  'printers.commandStatus.': { enum: 'KioskCommandStatus' },
  'supplies.event.': { enum: 'SupplyEventType' },
  'operator.taskType.': { enum: 'ServiceTaskType' },
  'operator.priority.': { enum: 'TaskPriority' },
  'operator.taskStatus.': { enum: 'ServiceTaskStatus' },
  'operator.resource.': { enum: 'SupplyResource' },
  'operator.unit.': { enum: 'SupplyUnit' },
  'operator.itemType.': { enum: 'InventoryItemType' },
  'supplies.mode.': { enum: 'PaperRefillMode' },
  'supplies.modeHint.': { enum: 'PaperRefillMode' },
  'suspected.decision.': { enum: 'SuspectedJobDecision' },
  'customerDetail.fileSource.': { enum: 'FileSource' },
  'customerDetail.fileStatus.': { enum: 'FileStatus', except: { Ready: 'a ready file carries no status chip' } },
  'features.platform.': { enum: 'FeaturePlatform' },
  'formats.group.': { values: ['Pdf', 'TextDocument', 'Spreadsheet', 'Presentation', 'Image', 'PageDescription'] },
  'formats.route.': { values: ['Pdf', 'TextDocument', 'Spreadsheet', 'Presentation', 'Image', 'PageDescription'] },
  'fiscal.setting.': { values: ['Fiscal.Enabled', 'Fiscal.MaxPendingMinutes', 'Fiscal.OnlineTerminalId', 'Fiscal.TopUpReceiptMode'] },
  'dashboard.attention.': { values: ['offline', 'safeMode', 'maintenance', 'paused', 'notConfigured', 'noPaper', 'paperUnset', 'lowPaper'] },
  'promos.state.': { values: ['Active', 'Scheduled', 'Expired', 'Exhausted', 'Inactive'] },
  'money.voucherStatus.': { values: ['Active', 'Redeemed', 'Expired'] },
  'permissionGroup.': { values: ['dashboard', 'kiosks', 'printers', 'supplies', 'jobs', 'payments', 'incidents', 'users', 'config', 'system', 'operator'] },
  'supplies.paper': { values: ['A4', 'A3'] },
  'settings.description.': { settings: true },
  'features.flag.': { flags: true, suffixes: ['.name', '.description'] },
  'language.': { values: ['az', 'en', 'ru'] },
  // The kiosk and printer pages (T-212).
  'monitoring.incidentStatus.': { enum: 'IncidentStatus' },
  'monitoring.alertLevel.': { enum: 'SupplyAlertLevel' },
  'monitoring.resource.': { enum: 'SupplyResource' },
  'monitoring.channel.': { enum: 'AlertChannels', except: { None: 'a flag value meaning "no channel", never listed' } },
  'monitoring.group.': { values: ['app', 'system', 'network', 'peripherals', 'work', 'services'] },
  'monitoring.metric.': {
    values: [
      'version', 'startedAt', 'screen', 'session', 'lastCrashAt',
      'os', 'hostname', 'cpu', 'memory', 'systemDisk', 'spoolDisk', 'uptime', 'clockOffset',
      'connection', 'localIp', 'latency', 'hub', 'hubLastReconnectAt',
      'flash', 'qrScanner', 'screenSize',
      'queueLength', 'currentJob', 'jobsToday', 'sheetsToday', 'spoolFiles', 'failedFlashUploads', 'pendingCommands',
      'libreOffice', 'ghostscript', 'configurationAt', 'featureFlags',
    ],
  },
  'monitoring.chart.': { values: ['cpu', 'memory', 'latency', 'disk'] },
  'monitoring.printerState.': { values: ['stale', 'unreachable', 'stopped', 'attention', 'printing', 'ready', 'unknown'] },
  'monitoring.printerStateRaw.': { values: ['idle', 'printing', 'stopped'] },
  'monitoring.printer.': {
    values: [
      'manufacturer', 'model', 'serial', 'firmware', 'driver', 'connection', 'mac', 'uri',
      'lifetimePages', 'colorPages', 'monoPages',
      'color', 'duplex', 'paperSizes', 'trayNames', 'resolution', 'formats',
    ],
  },
  'monitoring.printerEvent.': {
    values: [
      'State', 'Online', 'Reasons', 'Error', 'SupplyReplaced', 'PaperRefilled',
      'JamStarted', 'JamEnded', 'PaperOutStarted', 'PaperOutEnded', 'CoverOpened', 'CoverClosed',
    ],
  },
  'monitoring.printers.filter.': { values: ['all', 'lowToner', 'faulty', 'unreachable'] },
  // The operations pages (T-213).
  'ops.inventory.movement.': { enum: 'InventoryMovementType' },
  'ops.inventory.quantityHint.': { enum: 'InventoryMovementType' },
  'ops.tasks.source.': { enum: 'ServiceTaskSource' },
};

// ---------------------------------------------------------------------------

function files(directory, wanted) {
  const found = [];
  for (const name of readdirSync(directory)) {
    const path = join(directory, name);
    if (statSync(path).isDirectory()) found.push(...files(path, wanted));
    else if (wanted(path)) found.push(path);
  }
  return found;
}

const problems = [];
const say = (text) => problems.push(text);

// --- the dictionaries -------------------------------------------------------

const dictionaryFiles = files(join(app, 'core', 'i18n'), (path) => path.endsWith('.ts') && !path.endsWith('.spec.ts'));
const keys = new Set();

for (const file of dictionaryFiles) {
  for (const match of readFileSync(file, 'utf8').matchAll(/^\s*'([A-Za-z][\w.-]*)':/gm)) {
    keys.add(match[1]);
  }
}

const namespaces = new Set([...keys].map((key) => key.split('.')[0]));

// --- 1. keys written out in the sources --------------------------------------

const permissions = new Set(
  [...readFileSync(join(app, 'core', 'auth', 'auth.models.ts'), 'utf8').matchAll(/'([a-z][\w.*-]*)'/g)].map((m) => m[1]),
);

const sources = files(app, (path) => (path.endsWith('.ts') || path.endsWith('.html'))
  && !path.endsWith('.spec.ts')
  && !path.includes(`${join('core', 'i18n')}`));

const literal = /['"]([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9_-]+)+)['"]/g;
// 'jobs.status.' + job.status, and 'supplies.paper' + tray.paper: a literal a value is added to.
const dynamic = /'([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9_-]+)*\.?)'\s*\+/g;
const usedPrefixes = new Map();

for (const file of sources) {
  const text = readFileSync(file, 'utf8');
  const name = relative(root, file).replace(/\\/g, '/');

  for (const match of text.matchAll(dynamic)) {
    if (namespaces.has(match[1].split('.')[0])) usedPrefixes.set(match[1], name);
  }

  for (const match of text.matchAll(literal)) {
    const key = match[1];

    if (!namespaces.has(key.split('.')[0]) || keys.has(key) || permissions.has(key) || usedPrefixes.has(key)) continue;

    // A route, a file name, a CSS class with dots in a selector string.
    if (/\.(ts|html|scss|css|png|svg|json|woff2)$/.test(key)) continue;

    const line = text.slice(0, match.index).split('\n').length;
    say(`${name}:${line}  '${key}' is used but is in no dictionary.`);
  }
}

// --- 2. keys put together from what the server sends --------------------------

function backendFiles() {
  return files(backend, (path) => path.endsWith('.cs') && !/[\\/](bin|obj)[\\/]/.test(path));
}

const csharp = backendFiles().map((path) => ({ path, text: readFileSync(path, 'utf8') }));

function enumMembers(name) {
  for (const { text } of csharp) {
    const start = text.search(new RegExp(`\\benum\\s+${name}\\b`));
    if (start < 0) continue;

    // Comments first: a brace in a doc comment is not the end of the enum.
    const code = text.slice(start).replace(/\/\/[^\n]*/g, '');
    const open = code.indexOf('{');
    const body = code.slice(open + 1, code.indexOf('}', open)).replace(/\[[^\]]*\]/g, '');

    return body.split(',').map((member) => member.split('=')[0].trim()).filter((member) => /^[A-Za-z_]\w*$/.test(member));
  }

  return null;
}

function settingKeys() {
  const { text } = csharp.find(({ path }) => path.endsWith('AdminSettings.cs')) ?? {};
  if (!text) return null;

  const constants = new Map([...text.matchAll(/public const string (\w+) = "([^"]+)";/g)].map((m) => [m[1], m[2]]));

  // Only the settings that are defined - listed with a default and a type - are shown.
  return [...new Set([...text.matchAll(/new\(\s*(\w+)\s*,/g)].map((m) => constants.get(m[1])).filter(Boolean))];
}

function flagKeys() {
  // The flags the backend seeds: the ("key", "name", [platforms]) rows of FeatureFlag.cs.
  const seed = csharp.find(({ path }) => path.endsWith('FeatureFlag.cs'));
  if (!seed) return null;

  return [...new Set([...seed.text.matchAll(/\(\s*"([a-z][a-z0-9]*(?:_[a-z0-9]+)+)"\s*,/g)].map((m) => m[1]))];
}

for (const [prefix, file] of usedPrefixes) {
  if (!(prefix in SERVER_VALUES)) {
    const known = [...keys].some((key) => key.startsWith(prefix));
    say(`${file}  '${prefix}' + … is put together at run time${known ? '' : ' and no key starts with it'}: list it in SERVER_VALUES of scripts/i18n-check.mjs with where its values come from.`);
  }
}

for (const [prefix, source] of Object.entries(SERVER_VALUES)) {
  if (!usedPrefixes.has(prefix)) {
    say(`SERVER_VALUES lists '${prefix}', but no source builds a key from it any more: remove the entry.`);
    continue;
  }

  const values = source.enum ? enumMembers(source.enum)
    : source.settings ? settingKeys()
      : source.flags ? flagKeys()
        : source.values;

  if (!values || values.length === 0) {
    say(`'${prefix}': its values could not be read (${source.enum ? 'enum ' + source.enum : source.settings ? 'AdminSettings.cs' : 'FeatureFlag.cs'} was not found in backend/src).`);
    continue;
  }

  for (const value of values) {
    if (source.except?.[value]) continue;

    for (const suffix of source.suffixes ?? ['']) {
      const key = `${prefix}${value}${suffix}`;
      if (!keys.has(key)) say(`'${key}' has no translation (${source.enum ?? (source.settings ? 'a setting' : source.flags ? 'a feature flag' : 'a value')}): the panel would show the key itself.`);
    }
  }
}

// ---------------------------------------------------------------------------

if (problems.length > 0) {
  for (const problem of problems) console.error(problem);
  console.error(`\ni18n-check: ${problems.length} problem(s).`);
  process.exit(1);
}

console.log(`i18n-check: ${keys.size} keys, ${sources.length} source files, ${usedPrefixes.size} run-time prefixes - every key the panel can show has a translation.`);
