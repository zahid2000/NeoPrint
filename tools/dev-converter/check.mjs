#!/usr/bin/env node
// Runs every kind of user file through the real stack and checks that all of
// it is parsed in the converter container, none of it in the API process
// (plan step 7, T-051; docs/adr/ADR-0005-isolated-converter.md).
//
//   node tools/dev-converter/check.mjs            # the whole check
//   node tools/dev-converter/check.mjs --down     # also: stop the converter, look at the answers, start it again
//
// Needs the compose stack up and the development account of
// docs/DEV_ENVIRONMENT.md section 4. No dependencies: Node 20 or later.

import { execFileSync } from 'node:child_process';
import { deflateSync } from 'node:zlib';

const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
const email = process.env.NEOPRINT_DEV_EMAIL ?? 'dev-user@neoprint.local';
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const withDown = process.argv.includes('--down');

const results = [];
let failed = false;

function report(name, ok, detail = '') {
  results.push({ name, ok, detail });
  if (!ok) failed = true;
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  - ' + detail : ''}`);
}

// ---------------------------------------------------------------------------
// Sample files, one per format group, made here so nothing has to be shipped.
// ---------------------------------------------------------------------------

const crcTable = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});

function crc32(bytes) {
  let c = 0xffffffff;
  for (const b of bytes) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function pdf(pages) {
  let body = '%PDF-1.4\n';
  const offsets = [];
  const object = (text) => { offsets.push(body.length); body += text; };
  const kids = Array.from({ length: pages }, (_, i) => `${3 + i} 0 R`).join(' ');

  object('1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n');
  object(`2 0 obj\n<< /Type /Pages /Count ${pages} /Kids [${kids}] >>\nendobj\n`);
  for (let i = 0; i < pages; i++) {
    object(`${3 + i} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >> \nendobj\n`);
  }

  const xref = body.length;
  body += `xref\n0 ${offsets.length + 1}\n0000000000 65535 f \n`;
  for (const offset of offsets) body += `${String(offset).padStart(10, '0')} 00000 n \n`;
  body += `trailer\n<< /Size ${offsets.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;

  return Buffer.from(body, 'latin1');
}

function png(width, height) {
  const chunk = (type, data) => {
    const head = Buffer.alloc(8);
    head.writeUInt32BE(data.length, 0);
    head.write(type, 4, 'latin1');
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(Buffer.concat([head.subarray(4), data])), 0);
    return Buffer.concat([head, data, crc]);
  };

  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header.set([8, 2, 0, 0, 0], 8); // 8-bit RGB

  const rows = Buffer.alloc(height * (1 + width * 3));
  for (let y = 0; y < height; y++) {
    const row = y * (1 + width * 3);
    for (let x = 0; x < width; x++) {
      rows.set([40 + (x * 200) / width, 90, 200 - (y * 150) / height], row + 1 + x * 3);
    }
  }

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(rows)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

function bmp(width, height) {
  const rowSize = Math.ceil((width * 3) / 4) * 4;
  const file = Buffer.alloc(54 + rowSize * height, 0x80);
  file.write('BM', 0, 'latin1');
  file.writeUInt32LE(file.length, 2);
  file.writeUInt32LE(0, 6);
  file.writeUInt32LE(54, 10);
  file.writeUInt32LE(40, 14);
  file.writeInt32LE(width, 18);
  file.writeInt32LE(height, 22);
  file.writeUInt16LE(1, 26);
  file.writeUInt16LE(24, 28);
  file.writeUInt32LE(0, 30);
  file.writeUInt32LE(rowSize * height, 34);
  file.writeUInt32LE(2835, 38);
  file.writeUInt32LE(2835, 42);
  file.writeUInt32LE(0, 46);
  file.writeUInt32LE(0, 50);
  return file;
}

/** A ZIP with stored entries: enough for an OpenDocument file. */
function zip(entries) {
  const locals = [];
  const central = [];
  let offset = 0;

  for (const [name, text] of entries) {
    const data = Buffer.from(text, 'utf8');
    const file = Buffer.from(name, 'utf8');
    const crc = crc32(data);

    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(file.length, 26);
    locals.push(local, file, data);

    const entry = Buffer.alloc(46);
    entry.writeUInt32LE(0x02014b50, 0);
    entry.writeUInt16LE(20, 4);
    entry.writeUInt16LE(20, 6);
    entry.writeUInt32LE(crc, 16);
    entry.writeUInt32LE(data.length, 20);
    entry.writeUInt32LE(data.length, 24);
    entry.writeUInt16LE(file.length, 28);
    entry.writeUInt32LE(offset, 42);
    central.push(entry, file);

    offset += 30 + file.length + data.length;
  }

  const directory = Buffer.concat(central);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(directory.length, 12);
  end.writeUInt32LE(offset, 16);

  return Buffer.concat([...locals, directory, end]);
}

function odp() {
  const type = 'application/vnd.oasis.opendocument.presentation';
  const ns = 'xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" '
    + 'xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" '
    + 'xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" '
    + 'xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0"';
  const slide = (n) => `<draw:page draw:name="s${n}"><draw:frame svg:x="2cm" svg:y="2cm" svg:width="20cm" svg:height="3cm">`
    + `<draw:text-box><text:p>NeoPrint slide ${n}</text:p></draw:text-box></draw:frame></draw:page>`;

  return zip([
    ['mimetype', type],
    ['content.xml', `<?xml version="1.0" encoding="UTF-8"?><office:document-content ${ns} office:version="1.2">`
      + `<office:body><office:presentation>${slide(1)}${slide(2)}</office:presentation></office:body></office:document-content>`],
    ['META-INF/manifest.xml', '<?xml version="1.0" encoding="UTF-8"?>'
      + '<manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.2">'
      + `<manifest:file-entry manifest:full-path="/" manifest:media-type="${type}"/>`
      + '<manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/></manifest:manifest>'],
  ]);
}

const samples = [
  { group: 'Pdf', name: 'check.pdf', type: 'application/pdf', bytes: pdf(3), pages: 3 },
  { group: 'TextDocument', name: 'check.txt', type: 'text/plain', bytes: Buffer.from('NeoPrint\n\nBir səhifəlik mətn.\n', 'utf8') },
  { group: 'TextDocument', name: 'check.rtf', type: 'application/rtf', bytes: Buffer.from('{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Liberation Serif;}}\\f0\\fs28 NeoPrint RTF\\par}', 'latin1') },
  { group: 'Spreadsheet', name: 'check.csv', type: 'text/csv', bytes: Buffer.from('ad,say\nA4,10\nA3,2\n', 'utf8') },
  { group: 'Presentation', name: 'check.odp', type: 'application/vnd.oasis.opendocument.presentation', bytes: odp(), pages: 2 },
  { group: 'Image', name: 'check.png', type: 'image/png', bytes: png(640, 400), pages: 1 },
  { group: 'Image', name: 'check.bmp', type: 'image/bmp', bytes: bmp(64, 48), pages: 1 },
  { group: 'PageDescription', name: 'check.ps', type: 'application/postscript', bytes: Buffer.from('%!PS-Adobe-3.0\n/Helvetica findfont 24 scalefont setfont\n72 720 moveto (NeoPrint) show\nshowpage\n', 'latin1'), pages: 1 },
];

// ---------------------------------------------------------------------------
// The API
// ---------------------------------------------------------------------------

let token = '';

async function call(method, path, { json, form, raw = false } = {}) {
  const headers = { Authorization: `Bearer ${token}`, 'Accept-Language': 'az' };
  let body;

  if (json !== undefined) {
    headers['Content-Type'] = 'application/json';
    body = JSON.stringify(json);
  } else if (form) {
    body = form;
  }

  const started = Date.now();
  const reply = await fetch(`${api}${path}`, { method, headers, body });
  const bytes = Buffer.from(await reply.arrayBuffer());
  const ms = Date.now() - started;

  if (raw) return { status: reply.status, bytes, ms, type: reply.headers.get('content-type') ?? '' };

  let data = null;
  try { data = bytes.length ? JSON.parse(bytes.toString('utf8')) : null; } catch { /* not JSON */ }
  return { status: reply.status, data, ms };
}

async function upload({ name, type, bytes }) {
  const form = new FormData();
  form.append('file', new Blob([bytes], { type }), name);
  return call('POST', '/api/files', { form });
}

async function waitFor(id, seconds = 120) {
  const deadline = Date.now() + seconds * 1000;
  let file;
  do {
    file = (await call('GET', `/api/files/${id}`)).data;
    if (file?.status === 'Ready' || file?.status === 'Failed') return file;
    await new Promise((resolve) => setTimeout(resolve, 500));
  } while (Date.now() < deadline);
  return file;
}

const isPng = (bytes) => bytes.length > 8 && bytes.readUInt32BE(0) === 0x89504e47;

function docker(...args) {
  return execFileSync('docker', args, { encoding: 'utf8', env: { ...process.env, MSYS_NO_PATHCONV: '1' } }).trim();
}

function tryDocker(...args) {
  try { return docker(...args); } catch (error) { return (error.stdout ?? '').toString().trim(); }
}

// ---------------------------------------------------------------------------
// The check
// ---------------------------------------------------------------------------

const login = await fetch(`${api}/api/auth/login`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email, password }),
});

if (!login.ok) {
  console.error(`Could not sign in as ${email}: ${login.status}. Is the stack up (docker compose up -d)?`);
  process.exit(2);
}

token = (await login.json()).accessToken;

const created = [];
const ready = {};
const timings = [];

console.log('\n== Every format group becomes Ready through the converter ==');

for (const sample of samples) {
  const sent = await upload(sample);

  if (sent.status !== 201) {
    report(`${sample.group}: ${sample.name} uploads`, false, `${sent.status} ${sent.data?.detail ?? ''}`);
    continue;
  }

  created.push(sent.data.id);
  const file = await waitFor(sent.data.id);
  const pagesOk = sample.pages === undefined || file?.pageCount === sample.pages;

  report(
    `${sample.group}: ${sample.name} is Ready`,
    file?.status === 'Ready' && pagesOk,
    `${file?.status}, ${file?.pageCount ?? '?'} page(s)${file?.failureReason ? ', ' + file.failureReason : ''}`);

  if (file?.status !== 'Ready') continue;
  ready[sample.name] = file;

  const page = await call('GET', `/api/files/${file.id}/pages/1.png?width=640`, { raw: true });
  timings.push(page.ms);
  report(`${sample.group}: page 1 of ${sample.name} is drawn`, page.status === 200 && isPng(page.bytes), `${page.status}, ${page.bytes.length} bytes, ${page.ms} ms`);
}

console.log('\n== Edits, scans and layouts ==');

if (ready['check.pdf']) {
  const edit = await call('POST', `/api/files/${ready['check.pdf'].id}/edit`, {
    json: { pages: [{ page: 3, rotate: 90 }, { page: 1, rotate: 0 }] },
  });
  const file = edit.status === 200 || edit.status === 201 ? await waitFor(ready['check.pdf'].id) : null;
  report('PDF pages: drop one, reorder, turn', file?.status === 'Ready' && file.pageCount === 2, `${edit.status}, ${file?.pageCount ?? '?'} page(s), ${edit.ms} ms`);
}

if (ready['check.png']) {
  const edit = await call('POST', `/api/files/${ready['check.png'].id}/edit`, {
    json: { image: { rotate: 90, grayscale: true, crop: { x: 0.1, y: 0.1, width: 0.8, height: 0.8 } } },
  });
  const file = edit.status === 200 || edit.status === 201 ? await waitFor(ready['check.png'].id) : null;
  report('Image: crop, turn, grey', file?.status === 'Ready', `${edit.status} ${edit.data?.detail ?? ''}, ${edit.ms} ms`);
}

if (ready['check.bmp'] && ready['check.odp']) {
  const layout = {
    version: 1, paper: 'A4', orientation: 'Portrait', marginMm: 10,
    pages: [{
      elements: [
        { image: `file:${ready['check.bmp'].id}`, xMm: 20, yMm: 20, widthMm: 80, heightMm: 60 },
        { image: `file:${ready['check.odp'].id}:2`, xMm: 20, yMm: 100, widthMm: 120, heightMm: 68 },
      ],
    }],
  };

  const made = await call('POST', '/api/files/layout', { json: { layout, fileName: 'converter-check' } });
  if (made.data?.id) created.push(made.data.id);
  const file = made.status === 201 ? await waitFor(made.data.id) : null;
  report('Layout: an image file and a page of a presentation on one sheet', file?.status === 'Ready' && file.pageCount === 1, `${made.status} ${made.data?.detail ?? ''}, ${made.ms} ms`);
}

{
  const pending = await call('GET', '/api/consents/pending?scan=true');
  const notices = (pending.data ?? []).map((doc) => ({ docType: doc.docType, version: doc.version }));
  if (notices.length) await call('POST', '/api/consents', { json: { consents: notices } });

  const form = new FormData();
  form.append('images', new Blob([png(1012, 638)], { type: 'image/png' }), 'front');
  form.append('images', new Blob([png(1012, 638)], { type: 'image/png' }), 'back');
  form.append('layout', JSON.stringify({
    version: 1, paper: 'A4', orientation: 'Portrait', marginMm: 10,
    pages: [{
      elements: [
        { image: 'front', xMm: 62.2, yMm: 60, widthMm: 85.6, heightMm: 53.98 },
        { image: 'back', xMm: 62.2, yMm: 140, widthMm: 85.6, heightMm: 53.98 },
      ],
    }],
  }));
  form.append('documentType', 'IdCard');
  form.append('fileName', 'converter-check scan');

  const scan = await call('POST', '/api/files/scan', { form });
  if (scan.data?.id) created.push(scan.data.id);
  const file = scan.status === 201 ? await waitFor(scan.data.id) : null;
  report('Scan: two pictures on one sheet at real size', file?.status === 'Ready' && file.pageCount === 1, `${scan.status} ${scan.data?.detail ?? ''}, ${scan.ms} ms`);
}

console.log('\n== Bad input is an answer, not a crash ==');

{
  const broken = await upload({ name: 'broken.pdf', type: 'application/pdf', bytes: Buffer.from('%PDF-1.4\nthis is not a document\n%%EOF\n', 'latin1') });
  if (broken.data?.id) created.push(broken.data.id);
  const file = broken.status === 201 ? await waitFor(broken.data.id) : null;
  report('A damaged PDF fails with words for its owner', file?.status === 'Failed' && Boolean(file.failureReason), `${file?.status}: ${file?.failureReason ?? ''}`);

  const after = await call('GET', '/health');
  const converter = after.data?.checks?.find((check) => check.name === 'converter');
  report('The converter is still healthy afterwards', converter?.status === 'Healthy', converter?.description ?? '');
}

console.log('\n== The API process holds no parser ==');

{
  const maps = tryDocker('exec', 'neoprint-api', 'sh', '-c', 'grep -c -i -E "libpdfium|libSkiaSharp|PdfSharp|NeoPrint.Converter|NeoPrint.Pdfium" /proc/1/maps');
  report('neoprint-api has loaded no PDFium, SkiaSharp, PDFsharp or converter tool', maps === '0', `${maps} mapping(s) in /proc/1/maps`);

  const log = tryDocker('logs', '--since', '10m', 'neoprint-converter');
  const operations = ['pdf-info', 'pdf-render', 'image-to-pdf', 'edit-pages', 'edit-image', 'layout-render']
    .filter((operation) => log.includes(`with ${operation}`));
  report('The converter container carried out all six tool operations', operations.length === 6, operations.join(', '));

  const sorted = [...timings].sort((a, b) => a - b);
  if (sorted.length) console.log(`      page images: median ${sorted[Math.floor(sorted.length / 2)]} ms, slowest ${sorted.at(-1)} ms (${sorted.length} requests)`);
}

if (withDown && ready['check.pdf']) {
  console.log('\n== With the converter stopped ==');

  docker('stop', 'neoprint-converter');
  console.log('      waiting for the heartbeat to go stale (25 s)');
  await new Promise((resolve) => setTimeout(resolve, 25_000));

  try {
    const id = ready['check.pdf'].id;

    const page = await call('GET', `/api/files/${id}/pages/2.png?width=320`, { raw: true });
    let problem = null;
    try { problem = JSON.parse(page.bytes.toString('utf8')); } catch { /* not JSON */ }
    report('A preview answers 503 at once, in words', page.status === 503 && problem?.code === 'files.processingUnavailable' && page.ms < 3000, `${page.status} in ${page.ms} ms: ${problem?.detail ?? ''}`);

    const edit = await call('POST', `/api/files/${id}/edit`, { json: { pages: [{ page: 1, rotate: 180 }] } });
    report('An edit answers 503 and saves nothing', edit.status === 503 && edit.data?.code === 'files.processingUnavailable', `${edit.status} in ${edit.ms} ms`);

    const still = (await call('GET', `/api/files/${id}`)).data;
    report('The file is as it was', still?.status === 'Ready' && still.pageCount === 2, `${still?.status}, ${still?.pageCount} page(s)`);

    const pdfBytes = await call('GET', `/api/files/${id}/pdf`, { raw: true });
    report('A finished PDF is still served', pdfBytes.status === 200 && pdfBytes.bytes.subarray(0, 5).toString('latin1') === '%PDF-', `${pdfBytes.status}`);

    const health = await call('GET', '/health');
    const converter = health.data?.checks?.find((check) => check.name === 'converter');
    report('Health says the converter is degraded', converter?.status === 'Degraded', converter?.description ?? '');
  } finally {
    docker('start', 'neoprint-converter');
  }

  await new Promise((resolve) => setTimeout(resolve, 8_000));

  const again = await call('GET', `/api/files/${ready['check.pdf'].id}/pages/2.png?width=320`, { raw: true });
  report('The same preview works once the converter is back', again.status === 200 && isPng(again.bytes), `${again.status}, ${again.ms} ms`);
}

for (const id of created) {
  await call('DELETE', `/api/files/${id}`);
}

console.log(`\n${failed ? 'FAILED' : 'OK'}: ${results.filter((r) => r.ok).length} of ${results.length} checks passed; ${created.length} test file(s) deleted.`);
process.exit(failed ? 1 : 0);
