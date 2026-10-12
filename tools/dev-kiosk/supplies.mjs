#!/usr/bin/env node
// Walks plan section 9.9 on the development stack: a kiosk's paper and toner,
// the alerts and service tasks they raise, and the warehouse - what the
// panels' "Sərf", "Servis tapşırıqları" and "Anbar" pages will do (plan
// sections 2.15, 8A.3, 8B).
//
//   node tools/dev-kiosk/supplies.mjs                         # KSK-9002: trays, levels, forecast, alerts, tasks
//   node tools/dev-kiosk/supplies.mjs show KSK-0001           # another kiosk
//   node tools/dev-kiosk/supplies.mjs set 195                 # "the tray was counted: 195 sheets" (A4)
//   node tools/dev-kiosk/supplies.mjs rules                   # the alert rules
//   node tools/dev-kiosk/supplies.mjs tasks                   # the operator's tasks (dev-operator)
//   node tools/dev-kiosk/supplies.mjs start <taskId>          # "Başla", as the operator
//   node tools/dev-kiosk/supplies.mjs complete <taskId> [itemId] [quantity]   # "Tamamla": the tray is filled
//   node tools/dev-kiosk/supplies.mjs inventory               # the warehouse
//   node tools/dev-kiosk/supplies.mjs item <sku> "<name>" <type> <minStock>   # a new item (PaperA4Ream, Toner ...)
//   node tools/dev-kiosk/supplies.mjs move <itemId> <type> <quantity>         # Purchase, WriteOff, Adjustment ...
//
// The evaluator looks at a kiosk when its counter moves and every five minutes;
// the forecast runs hourly (docs/DEV_ENVIRONMENT.md section 7n).
//
// Needs the compose stack up and the development accounts of
// docs/DEV_ENVIRONMENT.md section 4. No dependencies: Node 20 or later.
import { randomUUID } from 'node:crypto';

const api = process.env.NEOPRINT_API ?? 'http://localhost:5080';
const password = process.env.NEOPRINT_DEV_PASSWORD ?? 'DevTest!2026';
const admin = process.env.NEOPRINT_DEV_SUPERADMIN_EMAIL ?? 'dev-superadmin@neoprint.local';
const operator = process.env.NEOPRINT_DEV_OPERATOR_EMAIL ?? 'dev-operator@neoprint.local';
const [command = 'show', ...args] = process.argv.slice(2);
const asOperator = ['tasks', 'start', 'complete'].includes(command);

async function signIn(email) {
  const login = await fetch(`${api}/api/auth/admin/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  });

  if (!login.ok) {
    console.error(
      login.status === 429
        ? 'Too many sign-ins in a minute (the API allows 10): wait a minute and run it again.'
        : `Could not sign in as ${email}: ${login.status}. Is the stack up (docker compose up -d)?`,
    );
    process.exit(2);
  }

  return (await login.json()).accessToken;
}

const token = await signIn(asOperator ? operator : admin);

async function call(method, path, body) {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${token}`,
      'Accept-Language': 'az',
      'Content-Type': 'application/json',
      'Idempotency-Key': randomUUID(),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const answer = await response.json().catch(() => ({}));

  if (!response.ok) {
    const reasons = Object.values(answer.errors ?? {}).flat().join(' ');
    console.error(`${method} ${path} was refused: ${response.status} ${reasons || answer.detail || answer.title || ''}`);
    process.exit(1);
  }

  return answer;
}

const time = (value) => (value ? new Date(value).toLocaleString('az-AZ', { hour12: false }) : '—');
const supply = (row) => `${row.resource}${row.variant ? ` (${row.variant})` : ''}`;

async function kioskOf(code) {
  const kiosk = (await call('GET', '/api/admin/kiosks')).find((each) => each.code === code);

  if (!kiosk) {
    console.error(`There is no kiosk ${code}.`);
    process.exit(1);
  }

  return kiosk;
}

function printTask(task) {
  console.log(
    `  ${task.status.padEnd(10)} ${task.priority.padEnd(8)} ${task.type.padEnd(13)} ${(task.kioskCode ?? '').padEnd(9)} ${(task.assignedToName ?? '(pool)').padEnd(16)} ${task.id}`,
  );
  if (task.notes) console.log(`             ${task.notes}`);
  if (task.completion) console.log(`             done: ${JSON.stringify(task.completion)}`);
  if (task.cancelReason) console.log(`             cancelled: ${task.cancelReason}`);
}

switch (command) {
  case 'show': {
    const kiosk = await kioskOf(args[0] ?? 'KSK-9002');
    const supplies = await call('GET', `/api/admin/kiosks/${kiosk.id}/supplies?limit=5`);

    console.log(`${kiosk.code} is ${kiosk.status}`);
    for (const [paper, stock] of Object.entries(supplies.paper)) {
      console.log(`  ${paper}: ${stock.current} / ${stock.capacity} sheets, last refill ${time(stock.lastRefillAt)}`);
    }
    for (const level of supplies.levels) console.log(`  ${supply(level)}: ${level.percent} %  (read ${time(level.readAt)})`);

    console.log('Forecast:');
    for (const forecast of supplies.forecasts) {
      console.log(`  ${supply(forecast).padEnd(28)} ${String(forecast.perDay).padStart(7)} ${forecast.unit === 'Sheets' ? 'sheets' : '%'}/day  runs out ${forecast.runsOutAt ? time(forecast.runsOutAt) : '— (nothing used, or more than a year)'}`);
    }

    console.log(`Alerts: ${supplies.alerts.length === 0 ? 'none' : ''}`);
    for (const alert of supplies.alerts) {
      console.log(`  ${alert.level.padEnd(8)} ${supply(alert).padEnd(28)} ${alert.value} ${alert.unit}  since ${time(alert.levelSince)}  task ${alert.serviceTaskId ?? '—'}`);
    }

    const tasks = await call('GET', `/api/admin/service-tasks?kioskId=${kiosk.id}&pageSize=5`);
    console.log(`Service tasks (${tasks.total}):`);
    tasks.items.forEach(printTask);
    break;
  }

  case 'set': {
    const kiosk = await kioskOf(args[1] ?? 'KSK-9002');
    const sheets = Number(args[0]);
    const supplies = await call('POST', `/api/admin/kiosks/${kiosk.id}/paper/refill`, { paper: 'A4', mode: 'Exact', sheets });
    console.log(`${kiosk.code}: A4 is ${supplies.paper.A4.current} / ${supplies.paper.A4.capacity}. The evaluator looks at it now (run "show" in a few seconds).`);
    break;
  }

  case 'rules':
    for (const rule of await call('GET', '/api/admin/supply-rules')) {
      console.log(
        `  ${rule.isActive ? ' ' : 'x'} ${rule.name.padEnd(22)} ${rule.resource}${rule.resourceVariant ? `:${rule.resourceVariant}` : ''}  warning < ${rule.warningThreshold}, critical < ${rule.criticalThreshold} ${rule.unit}  ${rule.isGlobal ? 'every kiosk' : `${rule.kioskIds.length} kiosk(s)`}  task: ${rule.createServiceTask ? rule.taskPriority : 'no'}  ${rule.channels.join('+')}`,
      );
    }
    break;

  case 'tasks': {
    const tasks = await call('GET', '/api/admin/service-tasks?mine=true&pageSize=20');
    console.log(`${operator}: ${tasks.total} task(s)`);
    tasks.items.forEach(printTask);
    break;
  }

  case 'start':
    printTask(await call('POST', `/api/admin/service-tasks/${args[0]}/start`));
    break;

  case 'complete':
    printTask(
      await call('POST', `/api/admin/service-tasks/${args[0]}/complete`, {
        refillMode: 'Full',
        inventoryItemId: args[1] ?? null,
        quantity: args[1] ? Number(args[2] ?? 1) : null,
      }),
    );
    break;

  case 'inventory':
    for (const item of await call('GET', '/api/admin/inventory/items?includeInactive=true')) {
      console.log(`  ${item.isLow ? 'LOW' : '   '} ${item.sku.padEnd(18)} ${item.name.padEnd(28)} ${String(item.stock).padStart(6)} ${item.unit} (min ${item.minStock})  ${item.type}  ${item.id}`);
    }
    break;

  case 'item': {
    const [sku, name, type, minStock] = args;
    const item = await call('POST', '/api/admin/inventory/items', {
      sku, name, type, compatiblePrinterModels: [], unit: type.startsWith('Paper') ? 'bağlama' : 'ədəd',
      sheetsPerUnit: type.startsWith('Paper') ? 500 : null, minStock: Number(minStock ?? 0), location: 'Anbar', isActive: true,
    });
    console.log(`${item.sku} ${item.name}: ${item.id}`);
    break;
  }

  case 'move': {
    const item = await call('POST', '/api/admin/inventory/movements', { itemId: args[0], type: args[1], quantity: Number(args[2]) });
    console.log(`${item.sku}: ${item.stock} ${item.unit}${item.isLow ? '  (below its minimum)' : ''}`);
    break;
  }

  default:
    console.error(`Unknown command "${command}". See the top of this file.`);
    process.exit(1);
}
