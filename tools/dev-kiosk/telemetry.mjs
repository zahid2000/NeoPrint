#!/usr/bin/env node
// Prints what a kiosk last reported about itself, as the admin panel reads it
// (plan section 7.9): the snapshot, whether it is stale, and the last points
// of the graphs.
//
//   node tools/dev-kiosk/telemetry.mjs             # KSK-9002, the Linux container kiosk
//   node tools/dev-kiosk/telemetry.mjs KSK-0001    # another kiosk, by its code
//
// Needs the compose stack up and the development admin of
// docs/DEV_ENVIRONMENT.md section 4. No dependencies: Node 20 or later.

const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
const email = process.env.NEOPRINT_DEV_ADMIN_EMAIL ?? 'dev-admin@neoprint.local';
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const code = process.argv[2] ?? 'KSK-9002';

const login = await fetch(`${api}/api/auth/admin/login`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email, password }),
});

if (!login.ok) {
  console.error(`Could not sign in as ${email}: ${login.status}. Is the stack up (docker compose up -d)?`);
  process.exit(2);
}

const headers = { Authorization: `Bearer ${(await login.json()).accessToken}` };
const get = async (path) => (await fetch(`${api}${path}`, { headers })).json();

const kiosk = (await get('/api/admin/kiosks')).find((item) => item.code === code);

if (!kiosk) {
  console.error(`No kiosk with the code ${code}.`);
  process.exit(1);
}

const view = await get(`/api/admin/kiosks/${kiosk.id}/telemetry`);
const gb = (bytes) => (bytes == null ? 'no data' : `${(bytes / 1024 ** 3).toFixed(1)} GB`);
const show = (value, unit = '') => (value == null ? 'no data' : `${value}${unit}`);

console.log(`${kiosk.code}  ${view.status.status}  last heartbeat ${view.status.lastHeartbeatAt ?? 'never'}  open incidents ${view.status.openIncidents}`);

if (!view.sample) {
  console.log('The kiosk has not reported any telemetry yet.');
  process.exit(0);
}

const { app, system, network, peripherals, work, services, timestamp, breaches } = view.sample;

console.log(`Reported ${timestamp}${view.stale ? '  (STALE: older than three intervals)' : ''}, every ${view.intervalSeconds} s`);
console.log(`  app       v${show(app.version)}, started ${show(app.startedAt)}, screen ${show(app.screen)}, session ${app.session ?? 'nobody'}, last crash ${app.lastCrashAt ?? 'none'}`);
console.log(`  system    ${show(system.os)} (${show(system.osVersion)}) on ${show(system.hostname)}; CPU ${show(system.cpuPercent, '%')}, memory ${show(system.memoryUsedPercent, '%')} of ${gb(system.memoryTotalBytes)}`);
console.log(`            disk free: system ${gb(system.systemDiskFreeBytes)}, spool ${gb(system.spoolDiskFreeBytes)}; up ${show(system.uptimeSeconds, ' s')}; clock off by ${show(system.clockOffsetMs, ' ms')}`);
console.log(`  network   ${show(network.connectionType)}${network.wifiSignalPercent == null ? '' : ` ${network.wifiSignalPercent}%`}, ${show(network.localIp)}; backend ${show(network.backendLatencyMs, ' ms')}; hub ${network.hubConnected == null ? 'no data' : network.hubConnected ? 'connected' : `down ${show(network.hubDisconnectedSeconds, ' s')}`}`);
console.log(`  hardware  flash ${peripherals.flashInserted == null ? 'no data' : peripherals.flashInserted ? 'inserted' : 'none'}; scanner last read ${peripherals.qrScannerLastReadAt ?? 'never'}; screen ${show(peripherals.screenWidth)}x${show(peripherals.screenHeight)}`);
console.log(`  work      queue ${show(work.queueLength)}, printing ${work.currentJob ?? 'nothing'}; today ${show(work.jobsToday)} job(s), ${show(work.sheetsToday)} sheet(s); spool ${show(work.spoolFiles)} file(s); flash uploads waiting ${show(work.failedFlashUploads)}; commands in progress ${show(work.pendingCommands)}`);
console.log(`  services  LibreOffice ${show(services.libreOffice)}, Ghostscript ${show(services.ghostscript)}; configuration of ${show(services.configurationAt)}; flags ${show(services.featureFlags)}`);
console.log(`  paper     ${Object.entries(view.status.paper).map(([size, stock]) => `${size} ${stock.current}/${stock.capacity}`).join(', ')}`);
console.log(`  over a limit: ${Object.keys(breaches ?? {}).length ? Object.entries(breaches).map(([name, since]) => `${name} since ${since}`).join(', ') : 'nothing'}`);

const history = await get(`/api/admin/kiosks/${kiosk.id}/telemetry/history?points=12`);
console.log(`\nLast hour, ${history.length} point(s):`);
for (const point of history) {
  console.log(`  ${point.at}  CPU ${show(point.cpuPercent, '%')}  memory ${show(point.memoryUsedPercent, '%')}  backend ${show(point.backendLatencyMs, ' ms')}  queue ${show(point.queueLength)}  (${point.samples} sample(s))`);
}

// What the kiosk's printers said about themselves (plan section 7.10).
const printers = await get(`/api/admin/kiosks/${kiosk.id}/printers/telemetry`);
console.log(`\nPrinters, ${printers.length}:`);

for (const { sample: printer, stale } of printers) {
  const unknown = 'the printer does not report this';
  console.log(`  ${printer.printer.printerName}${printer.selected ? '  (selected)' : ''}${stale ? '  (STALE)' : ''}  read ${printer.timestamp}`);
  console.log(`    identity   ${[printer.manufacturer, printer.model].filter(Boolean).join(' ') || unknown}; serial ${printer.serialNumber ?? unknown}; firmware ${printer.firmware ?? unknown}; driver ${printer.driverName ?? unknown}${printer.driverVersion ? ' ' + printer.driverVersion : ''}`);
  console.log(`    connection ${printer.connection ?? unknown}${printer.address ? ' ' + printer.address : ''}${printer.uri ? '  ' + printer.uri : ''}`);
  console.log(`    state      ${printer.state ?? unknown}, ${printer.online == null ? unknown : printer.online ? 'online' : 'OFFLINE'}; reasons: ${printer.stateReasons?.length ? printer.stateReasons.join(', ') : 'none'}${printer.lastError ? '; last error: ' + printer.lastError : ''}`);
  console.log(`    supplies   ${printer.supplies?.length ? printer.supplies.map((s) => `${s.name} [${s.type}${s.color ? ' ' + s.color : ''}] ${s.levelPercent == null ? 'level not reported' : s.levelPercent + '%'}`).join('; ') : unknown}`);
  console.log(`    trays      ${printer.trays?.length ? printer.trays.map((t) => `${t.name}${t.media ? ' ' + t.media : ''} ${t.levelPercent == null ? 'level not reported' : t.levelPercent + '%'}`).join('; ') : unknown}`);
  console.log(`    counters   lifetime ${printer.lifetimePages ?? unknown}`);
  console.log(`    can do     colour ${show(printer.features?.color)}, duplex ${show(printer.features?.duplex)}, ${show(printer.features?.resolutionDpi, ' dpi')}; paper ${printer.features?.paperSizes?.join(', ') || unknown}; native ${printer.features?.nativeFormats?.join(', ') || unknown}`);
  console.log(`    spooler    ${printer.spoolerJobs?.length ? printer.spoolerJobs.map((j) => `${j.id} ${j.state ?? ''} ${j.submittedAt ?? ''}`).join('; ') : 'empty'}`);

  const events = await get(`/api/admin/kiosks/${kiosk.id}/printers/telemetry/events?printer=${encodeURIComponent(printer.printer.printerName)}`);
  for (const event of events.slice(0, 5)) {
    console.log(`    ${event.at}  ${event.kind}: ${event.from ?? '-'} -> ${event.to ?? '-'}`);
  }
}
