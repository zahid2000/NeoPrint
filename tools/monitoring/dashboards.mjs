#!/usr/bin/env node
// ---------------------------------------------------------------------------
// NeoPrint — the Grafana dashboards (T-477, docs/MONITORING.md).
//
// The dashboards are written here, as code, and the JSON Grafana reads is
// generated from it - a panel is a few lines, not a hundred of JSON:
//
//   node tools/monitoring/dashboards.mjs          # writes the JSON files
//   node tools/monitoring/dashboards.mjs --check  # fails if they are stale (CI)
//
// Metric names are what the OpenTelemetry Collector's Prometheus exporter
// makes of the OpenTelemetry ones: dots become underscores, the unit becomes a
// suffix (_seconds, _bytes), counters end in _total.
// ---------------------------------------------------------------------------
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const out = join(root, 'docker', 'monitoring', 'grafana', 'dashboards');

const DS = { type: 'prometheus', uid: 'prometheus' };
const API = 'job="neoprint-api"';

// ---- panel builders --------------------------------------------------------

let nextId = 1;

function target(expr, legend, refId) {
  return { datasource: DS, expr, legendFormat: legend ?? '', refId, range: true };
}

function targets(queries) {
  return queries.map(([expr, legend], i) => target(expr, legend, String.fromCharCode(65 + i)));
}

/** A big number. */
function stat(title, expr, { unit = 'short', w = 4, thresholds, description, decimals } = {}) {
  return {
    id: nextId++,
    type: 'stat',
    title,
    description,
    datasource: DS,
    gridPos: { w, h: 4 },
    targets: targets([[expr, '']]),
    options: {
      reduceOptions: { calcs: ['lastNotNull'], fields: '', values: false },
      colorMode: 'background',
      graphMode: 'area',
      textMode: 'value',
    },
    fieldConfig: {
      defaults: {
        unit,
        decimals,
        noValue: '0',
        color: { mode: 'thresholds' },
        thresholds: {
          mode: 'absolute',
          steps: thresholds ?? [{ color: 'green', value: null }],
        },
      },
      overrides: [],
    },
  };
}

/** A line graph over time. */
function graph(title, queries, { unit = 'short', w = 12, h = 8, stack = false, description, min } = {}) {
  return {
    id: nextId++,
    type: 'timeseries',
    title,
    description,
    datasource: DS,
    gridPos: { w, h },
    targets: targets(queries),
    options: {
      legend: { displayMode: 'table', placement: 'bottom', calcs: ['lastNotNull', 'max'] },
      tooltip: { mode: 'multi', sort: 'desc' },
    },
    fieldConfig: {
      defaults: {
        unit,
        min,
        custom: {
          drawStyle: 'line',
          lineWidth: 1,
          fillOpacity: stack ? 30 : 10,
          showPoints: 'never',
          stacking: { mode: stack ? 'normal' : 'none', group: 'A' },
        },
      },
      overrides: [],
    },
  };
}

/** A table of the latest values, one row per series. */
function table(title, expr, { unit = 'short', w = 12, h = 8, label, valueTitle, description } = {}) {
  return {
    id: nextId++,
    type: 'table',
    title,
    description,
    datasource: DS,
    gridPos: { w, h },
    targets: [{ ...target(expr, '', 'A'), instant: true, range: false, format: 'table' }],
    transformations: [
      { id: 'organize', options: { excludeByName: { Time: true }, renameByName: { [label]: label, Value: valueTitle ?? 'Dəyər' } } },
    ],
    options: { showHeader: true, sortBy: [{ displayName: valueTitle ?? 'Dəyər', desc: true }] },
    fieldConfig: { defaults: { unit, noValue: '0' }, overrides: [] },
  };
}

function row(title) {
  return { id: nextId++, type: 'row', title, collapsed: false, gridPos: { w: 24, h: 1 }, panels: [] };
}

/** Lays panels out left to right, wrapping at 24 columns. */
function layout(panels) {
  let x = 0;
  let y = 0;
  let rowHeight = 0;

  for (const panel of panels) {
    const { w, h } = panel.gridPos;
    if (panel.type === 'row' || x + w > 24) {
      y += rowHeight;
      x = 0;
      rowHeight = 0;
    }
    panel.gridPos = { x, y, w, h };
    x += w;
    rowHeight = Math.max(rowHeight, h);
    if (panel.type === 'row') {
      y += h;
      x = 0;
      rowHeight = 0;
    }
  }
  return panels;
}

function dashboard(uid, title, description, panels, { refresh = '30s', from = 'now-6h' } = {}) {
  nextId = 1;
  const built = panels();
  return {
    uid,
    title,
    description,
    tags: ['neoprint'],
    timezone: 'browser',
    editable: false,
    graphTooltip: 1,
    refresh,
    schemaVersion: 41,
    version: 1,
    time: { from, to: 'now' },
    links: [{ title: 'NeoPrint', type: 'dashboards', tags: ['neoprint'], asDropdown: true, includeVars: false, keepTime: true }],
    templating: { list: [] },
    annotations: { list: [] },
    panels: layout(built),
  };
}

// ---- queries ---------------------------------------------------------------

const requests = `http_server_request_duration_seconds_count{${API}}`;
const buckets = `http_server_request_duration_seconds_bucket{${API}}`;
const errors5xx = `sum(rate(${requests.replace('}', ',http_response_status_code=~"5.."}')}[5m]))`;
const allRate = `sum(rate(${requests}[5m]))`;
const quantile = (q, by = '') =>
  `histogram_quantile(${q}, sum by (le${by ? `, ${by}` : ''}) (rate(${buckets}[5m])))`;

// MassTransit's dead-letter queues: <queue>_error (failed after the retries)
// and <queue>_skipped (no consumer took it).
const dlq = 'queue=~".*_(error|skipped)"';

const red = [{ color: 'green', value: null }, { color: 'red', value: 1 }];
const amberRed = (amber, redAt) => [
  { color: 'green', value: null },
  { color: 'orange', value: amber },
  { color: 'red', value: redAt },
];

// ---- dashboards ------------------------------------------------------------

const dashboards = {
  'neoprint-overview.json': dashboard(
    'neoprint-overview',
    'NeoPrint — Ümumi baxış',
    'Bir baxışda: API, növbələr, pul və kiosklar. Qırmızı = baxmaq lazımdır.',
    () => [
      row('API'),
      stat('Sorğu / san', allRate, { unit: 'reqps', decimals: 1 }),
      stat('5xx xətaları (%)', `100 * ${errors5xx} / clamp_min(${allRate}, 0.001)`, {
        unit: 'percent', decimals: 2, thresholds: amberRed(1, 5),
        description: 'Son 5 dəqiqədə serverin öz xətası ilə bitən sorğuların payı.',
      }),
      stat('Gecikmə p95', quantile(0.95), { unit: 's', decimals: 2, thresholds: amberRed(1, 3) }),
      stat('Aktiv bağlantılar', `sum(kestrel_active_connections{${API}})`),
      stat('API yaddaşı', `sum(dotnet_process_memory_working_set_bytes{${API}})`, { unit: 'bytes', thresholds: amberRed(1.5e9, 2.5e9) }),
      stat('API nüsxələri', `count(count by (exported_instance) (dotnet_process_memory_working_set_bytes{${API}}))`, {
        thresholds: [{ color: 'red', value: null }, { color: 'green', value: 1 }],
        description: 'Metrika göndərən API prosesləri. 0 = API işləmir və ya kollektora çatmır.',
      }),
      row('Növbələr'),
      stat('Dead-letter mesajları', `sum(rabbitmq_detailed_queue_messages{${dlq}})`, {
        thresholds: red, description: 'MassTransit-in _error və _skipped növbələrindəki mesajlar. 0 olmalıdır.',
      }),
      stat('Gözləyən mesajlar', `sum(rabbitmq_detailed_queue_messages_ready)`, { thresholds: amberRed(100, 1000) }),
      stat('RabbitMQ', 'up{job="rabbitmq"}', {
        thresholds: [{ color: 'red', value: null }, { color: 'green', value: 1 }],
        description: '1 = Prometheus RabbitMQ-ya çatır.',
      }),
      row('Pul və sənədlər'),
      stat('Uğursuz refund', 'sum(neoprint_refunds_open{status="Failed"})', { thresholds: red }),
      stat('Gözləyən refund', 'sum(neoprint_refunds_open{status=~"Pending|Processing"})', { thresholds: amberRed(5, 20) }),
      stat('Refund-u alınmayan ödəniş', 'sum(neoprint_payments_refund_failed)', { thresholds: red }),
      stat('Uğursuz fiskal çek', 'sum(neoprint_fiscal_receipts_open{status="Failed"})', { thresholds: red }),
      stat('Gözləyən fiskal çek', 'sum(neoprint_fiscal_receipts_open{status="Pending"})', { thresholds: amberRed(5, 20) }),
      stat('Storage sync xətaları', 'sum(neoprint_storage_sync_failures)', { thresholds: red }),
      row('Kiosklar və incident-lər'),
      stat('Online kiosk', 'sum(neoprint_kiosks{status="Online"})', { thresholds: [{ color: 'blue', value: null }] }),
      stat('Offline kiosk', 'sum(neoprint_kiosks{status="Offline"})', { thresholds: red }),
      stat('Texniki xidmətdə', 'sum(neoprint_kiosks{status=~"Maintenance|Paused"})', { thresholds: amberRed(1, 100) }),
      stat('Açıq incident (Critical)', 'sum(neoprint_incidents_open{severity="Critical"})', { thresholds: red }),
      stat('Açıq incident (hamısı)', 'sum(neoprint_incidents_open)', { thresholds: amberRed(1, 10) }),
      stat('Xəstə storage', 'sum(neoprint_storage_providers_unhealthy)', { thresholds: red }),
      row('Trend'),
      graph('Sorğular — cavab sinfinə görə', [
        [`sum by (status) (label_replace(rate(${requests}[5m]), "status", "\${1}xx", "http_response_status_code", "(.).."))`, '{{status}}'],
      ], { unit: 'reqps', stack: true }),
      graph('Gecikmə', [
        [quantile(0.5), 'p50'],
        [quantile(0.95), 'p95'],
        [quantile(0.99), 'p99'],
      ], { unit: 's' }),
    ],
  ),

  'neoprint-api.json': dashboard(
    'neoprint-api',
    'NeoPrint — API',
    'Endpoint-lər üzrə gecikmə və xətalar, proses resursları, xaricə gedən sorğular (bank, fiskal).',
    () => [
      row('Endpoint-lər'),
      table('Ən yavaş endpoint-lər (p95, son 1 saat)',
        `topk(15, histogram_quantile(0.95, sum by (le, http_route, http_request_method) (rate(${buckets}[1h]))))`,
        { unit: 's', label: 'http_route', valueTitle: 'p95' }),
      table('Ən çox 5xx verən endpoint-lər (son 1 saat)',
        `topk(15, sum by (http_route, http_request_method) (increase(${requests.replace('}', ',http_response_status_code=~"5.."}')}[1h])) > 0)`,
        { label: 'http_route', valueTitle: '5xx sayı' }),
      graph('p95 gecikmə — endpoint-ə görə (top 10)', [
        [`topk(10, histogram_quantile(0.95, sum by (le, http_route) (rate(${buckets}[5m]))))`, '{{http_route}}'],
      ], { unit: 's' }),
      graph('4xx / 5xx', [
        [`sum(rate(${requests.replace('}', ',http_response_status_code=~"4.."}')}[5m]))`, '4xx'],
        [errors5xx, '5xx'],
      ], { unit: 'reqps' }),
      graph('Limitə düşən sorğular (429)', [
        [`sum by (http_route) (rate(${requests.replace('}', ',http_response_status_code="429"}')}[5m]))`, '{{http_route}}'],
      ], { unit: 'reqps', description: 'Sorğu limiti (rate limit) rədd edən sorğular.' }),
      graph('İşlənməmiş exception-lar', [
        [`sum by (error_type) (rate(aspnetcore_diagnostics_exceptions_total{${API}}[5m]))`, '{{error_type}}'],
      ], { unit: 'short' }),
      row('Bağlantılar'),
      graph('HTTP bağlantıları', [
        [`sum(kestrel_active_connections{${API}})`, 'aktiv'],
      ]),
      graph('SignalR bağlantıları (kiosk, admin, istifadəçi)', [
        [`sum(signalr_server_active_connections{${API}})`, 'aktiv'],
      ]),
      row('Proses'),
      graph('Yaddaş', [
        [`sum by (exported_instance) (dotnet_process_memory_working_set_bytes{${API}})`, 'working set {{exported_instance}}'],
        [`sum by (exported_instance) (dotnet_gc_last_collection_heap_size_bytes{${API}})`, 'GC heap {{exported_instance}}'],
      ], { unit: 'bytes', w: 8 }),
      graph('CPU', [
        [`sum by (exported_instance) (rate(dotnet_process_cpu_time_seconds_total{${API}}[5m]))`, '{{exported_instance}}'],
      ], { unit: 'percentunit', w: 8 }),
      graph('Thread pool növbəsi', [
        [`sum by (exported_instance) (dotnet_thread_pool_queue_length{${API}})`, '{{exported_instance}}'],
      ], { w: 8 }),
      row('Xaricə gedən sorğular'),
      graph('p95 — ünvana görə', [
        [`histogram_quantile(0.95, sum by (le, server_address) (rate(http_client_request_duration_seconds_bucket{${API}}[5m])))`, '{{server_address}}'],
      ], { unit: 's' }),
      graph('Uğursuz — ünvana görə', [
        [`sum by (server_address, error_type) (rate(http_client_request_duration_seconds_count{${API},error_type!=""}[5m]))`, '{{server_address}} {{error_type}}'],
      ], { unit: 'reqps' }),
    ],
  ),

  'neoprint-queues.json': dashboard(
    'neoprint-queues',
    'NeoPrint — Növbələr (RabbitMQ)',
    'Növbələrdə gözləyən, işlənən mesajlar və dead-letter növbələri.',
    () => [
      table('Dead-letter növbələri (indi)', `sum by (queue) (rabbitmq_detailed_queue_messages{${dlq}}) > 0`,
        { label: 'queue', valueTitle: 'Mesaj', description: 'Boşdursa hər şey qaydasındadır.' }),
      graph('Dead-letter mesajları', [
        [`sum by (queue) (rabbitmq_detailed_queue_messages{${dlq}})`, '{{queue}}'],
      ]),
      graph('Gözləyən mesajlar — növbəyə görə', [
        [`topk(10, sum by (queue) (rabbitmq_detailed_queue_messages_ready))`, '{{queue}}'],
      ]),
      graph('Təsdiqlənməmiş (işlənən) mesajlar', [
        [`topk(10, sum by (queue) (rabbitmq_detailed_queue_messages_unacked))`, '{{queue}}'],
      ]),
      table('Consumer-i olmayan növbələr', `sum by (queue) (rabbitmq_detailed_queue_consumers{queue!~".*_(error|skipped)"}) == 0`,
        { label: 'queue', valueTitle: 'Consumer', description: 'Mesajı oxuyan yoxdur — servis dayanıb ola bilər.' }),
      graph('Mesaj axını', [
        ['sum(rate(rabbitmq_global_messages_received_total[5m]))', 'qəbul'],
        ['sum(rate(rabbitmq_global_messages_delivered_total[5m]))', 'çatdırılıb'],
      ], { unit: 'ops' }),
      graph('RabbitMQ yaddaşı', [
        ['sum(rabbitmq_process_resident_memory_bytes)', 'istifadə'],
        ['sum(rabbitmq_resident_memory_limit_bytes)', 'limit'],
      ], { unit: 'bytes' }),
    ],
  ),

  'neoprint-operations.json': dashboard(
    'neoprint-operations',
    'NeoPrint — Əməliyyatlar',
    'Pul, sənəd və cihaz vəziyyəti: MongoDB-dən dəqiqədə bir sayılır.',
    () => [
      row('Refund və ödəniş'),
      graph('Bitməmiş refund-lar — statusa görə', [
        ['sum by (status) (neoprint_refunds_open)', '{{status}}'],
      ], { min: 0 }),
      graph('Refund-u alınmayan ödənişlər', [
        ['sum(neoprint_payments_refund_failed)', 'RefundFailed'],
      ], { min: 0 }),
      row('Fiskal'),
      graph('Verilməmiş fiskal çeklər — statusa görə', [
        ['sum by (status) (neoprint_fiscal_receipts_open)', '{{status}}'],
      ], { min: 0, w: 24 }),
      row('Storage'),
      graph('Storage sync — həll olunmamış obyektlər', [
        ['sum(neoprint_storage_sync_failures)', 'xəta'],
      ], { min: 0 }),
      graph('Xəstə storage provayderləri', [
        ['sum(neoprint_storage_providers_unhealthy)', 'xəstə'],
      ], { min: 0 }),
      graph('Təmizlənən fayllar', [
        ['sum(increase(neoprint_files_cleanup_deleted_total[1h]))', 'obyekt / saat'],
      ], { min: 0 }),
      graph('Boşaldılan yer', [
        ['sum(increase(neoprint_files_cleanup_deleted_bytes_total[1h]))', 'bayt / saat'],
      ], { unit: 'bytes', min: 0 }),
      row('Kiosklar və incident-lər'),
      graph('Kiosklar — statusa görə', [
        ['sum by (status) (neoprint_kiosks)', '{{status}}'],
      ], { stack: true, min: 0 }),
      graph('Açıq incident-lər — ciddiliyə görə', [
        ['sum by (severity) (neoprint_incidents_open)', '{{severity}}'],
      ], { stack: true, min: 0 }),
      row('Yoxlamalar'),
      graph('Uzlaşdırma tapıntıları (pul yoxlaması)', [
        ['sum by (check, kind) (increase(neoprint_reconciliation_findings_total[1h]))', '{{check}} {{kind}}'],
      ], { min: 0 }),
      graph('Atılan analitika hadisələri', [
        ['sum by (reason) (increase(neoprint_analytics_events_rejected_total[1h]))', '{{reason}}'],
      ], { min: 0 }),
    ],
  ),
};

// ---- write / check ---------------------------------------------------------

const check = process.argv.includes('--check');
let stale = 0;

for (const [file, content] of Object.entries(dashboards)) {
  const path = join(out, file);
  const json = `${JSON.stringify(content, null, 2)}\n`;

  if (check) {
    let current = '';
    try {
      current = readFileSync(path, 'utf8');
    } catch {
      // missing counts as stale
    }
    if (current !== json) {
      console.error(`stale: ${file} - run node tools/monitoring/dashboards.mjs`);
      stale++;
    }
  } else {
    writeFileSync(path, json);
    console.log(`wrote ${file}`);
  }
}

if (stale > 0) {
  process.exit(1);
}
