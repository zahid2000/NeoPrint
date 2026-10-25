import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { of } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { ServiceTaskDetail } from '../../core/api/step7.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { FakeHub } from '../../core/realtime/fake-hub.testing';
import { liveDebounceMs } from '../../core/realtime/live-refresh';
import { OperatorNotifyService } from '../operator/operator-notify.service';
import { AuditLogComponent, diff } from './audit-log.component';
import { IncidentsComponent } from './incidents.component';
import { InventoryComponent } from './inventory.component';
import { InventoryMovementDialogComponent } from './inventory.dialogs';
import { ServiceTasksComponent } from './service-tasks.component';
import { SupplyRulesComponent } from './supply-rules.component';
import { TaskDialogComponent } from './task.dialogs';

const kioskId = '11111111-1111-1111-1111-111111111111';
const kiosk = { id: kioskId, code: 'KSK-9002', name: 'Library', status: 'Online' };

let backend: HttpTestingController;
let hub: FakeHub;
const announced: string[][] = [];

function setUp(permissions: string[], options: { confirm?: { reason: string } | undefined; extra?: unknown[] } = {}): void {
  hub = new FakeHub();
  announced.length = 0;

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      { provide: AdminHubService, useValue: hub },
      {
        provide: AuthService,
        useValue: {
          admin: signal({ id: 'a-1' }),
          can: (permission: string) => permissions.includes(permission),
          canAny: (list: readonly string[]) => list.some((permission) => permissions.includes(permission)),
        },
      },
      {
        provide: OperatorNotifyService,
        useValue: {
          access: signal('granted'),
          enable: () => Promise.resolve(),
          announce: (title: string, body: string, tag: string) => announced.push([title, body, tag]),
        },
      },
      {
        provide: MatDialog,
        useValue: { open: () => ({ afterClosed: () => of('confirm' in options ? options.confirm : { reason: 'Kağız qoyuldu' }) }) },
      },
      ...((options.extra ?? []) as never[]),
    ],
  });

  backend = TestBed.inject(HttpTestingController);
}

function text(fixture: ComponentFixture<unknown>): string {
  return (fixture.nativeElement as HTMLElement).textContent ?? '';
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  // Answers that arrive together (Promise.all) finish a turn later.
  await new Promise((resolve) => setTimeout(resolve));
  await fixture.whenStable();
  fixture.detectChanges();
}

const page = <T>(items: T[]) => ({ items, total: items.length, page: 1, pageSize: 20 });

function incident(overrides: Record<string, unknown> = {}) {
  return {
    id: 'i-1',
    type: 'PaperOut',
    severity: 'Critical',
    status: 'Open',
    details: 'Printerdə kağız bitib',
    kioskId,
    kioskCode: 'KSK-9002',
    createdAt: '2026-10-16T09:00:00Z',
    ...overrides,
  };
}

function task(overrides: Partial<ServiceTaskDetail> = {}): ServiceTaskDetail {
  return {
    id: 't-1',
    kioskId,
    kioskCode: 'KSK-9002',
    type: 'RefillPaper',
    priority: 'Critical',
    status: 'Assigned',
    source: 'AlertRule',
    assignedTo: 'op-1',
    assignedToName: 'Operator One',
    createdAt: '2026-10-16T08:00:00Z',
    assignedAt: '2026-10-16T08:00:01Z',
    ...overrides,
  };
}

afterEach(() => {
  backend.verify();
  vi.useRealTimers();
});

describe('Incident-lər', () => {
  async function open(permissions: string[], options = {}, listed?: ReturnType<typeof incident>[]) {
    setUp(permissions, options);
    const fixture = TestBed.createComponent(IncidentsComponent);
    fixture.detectChanges();

    backend.expectOne('/api/admin/kiosks').flush([kiosk]);
    backend.match((request) => request.url === '/api/admin/incidents' && request.params.get('pageSize') === '5').forEach((r) => r.flush(page([incident()])));
    const list = backend.expectOne((request) => request.url === '/api/admin/incidents' && request.params.get('pageSize') === '20');
    expect(list.request.params.getAll('status')).toEqual(['Open', 'Acknowledged']);
    list.flush(page(listed ?? [incident(), incident({ id: 'i-2', severity: 'Medium', status: 'Acknowledged', details: 'Disk azdır' })]));
    await settle(fixture);

    return fixture;
  }

  it('lists what is open with its actions, and closes one with what was done', async () => {
    const fixture = await open([Permissions.incidentsView, Permissions.incidentsAcknowledge, Permissions.incidentsResolve]);
    const buttons = (): string[] => [...(fixture.nativeElement as HTMLElement).querySelectorAll('tr.mat-mdc-row button')].map((b) => b.textContent?.trim() ?? '');

    expect(text(fixture)).toContain('Printerdə kağız bitib');
    expect(text(fixture)).toContain('money.incidentSeverity.Critical');
    expect(text(fixture)).toContain('ops.incidents.openCount');
    // "Baxıram" only on the one nobody has taken; "Bağla" on both.
    expect(buttons().filter((label) => label === 'ops.incidents.acknowledge')).toHaveLength(1);
    expect(buttons().filter((label) => label === 'ops.incidents.resolve')).toHaveLength(2);

    const closing = fixture.componentInstance.resolve(incident() as never, new Event('click'));
    await Promise.resolve();
    const resolve = backend.expectOne('/api/admin/incidents/i-1/resolve');
    expect(resolve.request.body).toEqual({ note: 'Kağız qoyuldu' });
    resolve.flush(incident({ status: 'Resolved' }));
    await new Promise((r) => setTimeout(r));
    backend.expectOne((request) => request.url === '/api/admin/incidents' && request.params.get('pageSize') === '20').flush(page([]));
    await closing;
    // The count every page shows is asked for again too.
    backend.match((request) => request.url === '/api/admin/incidents').forEach((r) => r.flush(page([])));
  });

  it('says how often an open incident was seen again, and nothing for one seen once (T-326)', async () => {
    const fixture = await open([Permissions.incidentsView], {}, [
      incident({ id: 'i-3', type: 'UpdatePackageRejected', details: 'Paket yoxlamadan keçmədi', occurrences: 4, lastSeenAt: '2026-10-16T10:00:00Z' }),
      incident(),
    ]);

    const seen = (fixture.nativeElement as HTMLElement).querySelectorAll('[data-occurrences]');
    expect(seen).toHaveLength(1);
    expect(seen[0].textContent).toContain('monitoring.incidents.seen');
  });

  it('offers no action to an account that may only look', async () => {
    const fixture = await open([Permissions.incidentsView]);

    expect((fixture.nativeElement as HTMLElement).querySelectorAll('tr.mat-mdc-row button')).toHaveLength(0);
  });

  it('opens the orders an incident touched under its row', async () => {
    const fixture = await open([Permissions.incidentsView]);

    void fixture.componentInstance.toggle(fixture.componentInstance.rows()![0]);
    backend.expectOne('/api/admin/incidents/i-1').flush({
      incident: { ...incident(), acknowledgedAt: '2026-10-16T09:05:00Z' },
      affected: [
        {
          jobId: 'j-1',
          jobCode: 'A1B2C3',
          status: 'Cancelled',
          registered: true,
          paid: 1.2,
          currency: 'AZN',
          paymentStatus: 'Refunded',
          refunds: [{ id: 'r-1', status: 'Succeeded', amount: 1.2, destination: 'Wallet', reason: 'x', createdAt: '' }],
        },
      ],
    });
    await settle(fixture);

    expect(text(fixture)).toContain('A1B2C3');
    expect(text(fixture)).toContain('ops.incidents.acknowledged');
    expect(text(fixture)).toContain('money.refundStatus.Succeeded');
  });

  it('says a new Critical incident aloud and fetches the list again; a Medium one only fetches', async () => {
    const fixture = await open([Permissions.incidentsView]);
    vi.useFakeTimers();

    hub.emit('IncidentRaised', { incidentId: 'i-9', type: 'ConnectionLost', severity: 'Critical', kioskId });
    hub.emit('IncidentRaised', { incidentId: 'i-10', type: 'DiskLow', severity: 'Medium', kioskId });

    expect(announced).toEqual([['ops.incidents.notify.Critical', 'KSK-9002', 'incident:i-9']]);

    await vi.advanceTimersByTimeAsync(liveDebounceMs + 10);
    backend.match((request) => request.url === '/api/admin/incidents').forEach((r) => r.flush(page([])));
    fixture.detectChanges();
  });
});

describe('Servis tapşırıqları', () => {
  async function open(permissions: string[]) {
    setUp(permissions);
    const fixture = TestBed.createComponent(ServiceTasksComponent);
    fixture.detectChanges();

    backend.expectOne('/api/admin/kiosks').flush([kiosk]);
    backend.expectOne((request) => request.url === '/api/admin/service-tasks').flush(
      page([task({ dueAt: '2026-10-01T00:00:00Z' }), task({ id: 't-2', status: 'Open', assignedTo: null, assignedToName: null, priority: 'Normal' })]),
    );
    backend.expectOne('/api/admin/service-tasks/summary').flush({ open: 2, overdue: 1, unassigned: 1, completedLastWeek: 3, averageMinutes: 135 });
    await settle(fixture);

    return fixture;
  }

  it('shows the SLA figures, the late task and the pool, and asks for the pool on its own', async () => {
    const fixture = await open([Permissions.serviceTasksView]);

    expect(text(fixture)).toContain('ops.tasks.statOverdue');
    expect(text(fixture)).toContain('ops.tasks.late');
    expect(text(fixture)).toContain('monitoring.tasks.unassigned');
    expect(text(fixture), 'no "new task" without the permission').not.toContain('ops.tasks.new');
    expect((fixture.nativeElement as HTMLElement).querySelector('.ops__stat--danger')).not.toBeNull();

    fixture.componentInstance.setUnassigned(true);
    const pool = backend.expectOne((request) => request.url === '/api/admin/service-tasks');
    expect(pool.request.params.get('unassigned')).toBe('true');
    pool.flush(page([]));
    backend.expectOne('/api/admin/service-tasks/summary').flush({ open: 2, overdue: 1, unassigned: 1, completedLastWeek: 0 });
    await settle(fixture);

    expect(text(fixture)).toContain('ops.tasks.noneOpen');
  });

  it('follows a task that changed elsewhere', async () => {
    const fixture = await open([Permissions.serviceTasksView, Permissions.serviceTasksManage]);
    vi.useFakeTimers();

    hub.emit('ServiceTaskUpdated', task({ status: 'InProgress' }));
    await vi.advanceTimersByTimeAsync(liveDebounceMs + 10);

    backend.expectOne((request) => request.url === '/api/admin/service-tasks').flush(page([task({ status: 'InProgress' })]));
    backend.expectOne('/api/admin/service-tasks/summary').flush({ open: 1, overdue: 0, unassigned: 0, completedLastWeek: 0 });
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    expect(text(fixture)).toContain('operator.taskStatus.InProgress');
    expect(text(fixture)).toContain('ops.tasks.new');
  });
});

describe('Tapşırığın dialoqu', () => {
  it('gives the task to another and raises its priority, then closes', async () => {
    const closed: unknown[] = [];
    setUp([Permissions.serviceTasksView, Permissions.serviceTasksManage], {
      extra: [
        { provide: MAT_DIALOG_DATA, useValue: { task: task() } },
        { provide: MatDialogRef, useValue: { close: (value: unknown) => closed.push(value) } },
      ],
    });
    const fixture = TestBed.createComponent(TaskDialogComponent);
    fixture.detectChanges();
    backend.expectOne((request) => request.url === '/api/admin/service-tasks/assignees').flush([
      { id: 'op-2', fullName: 'Operator Two', operator: true, activeTasks: 0 },
      { id: 'op-1', fullName: 'Operator One', operator: true, activeTasks: 3 },
    ]);
    await settle(fixture);

    expect(text(fixture)).toContain('ops.tasks.step.assigned');

    fixture.componentInstance.assignTo.set('op-2');
    fixture.componentInstance.priority.set('High');
    const saving = fixture.componentInstance.save();

    const assign = backend.expectOne('/api/admin/service-tasks/t-1/assign');
    expect(assign.request.body).toEqual({ assignTo: 'op-2' });
    assign.flush(task({ assignedTo: 'op-2', assignedToName: 'Operator Two' }));
    await new Promise((r) => setTimeout(r));
    const update = backend.expectOne('/api/admin/service-tasks/t-1');
    expect(update.request.method).toBe('PUT');
    expect(update.request.body).toEqual({ priority: 'High', notes: null, dueAt: null });
    update.flush(task({ priority: 'High' }));
    await saving;

    expect(closed).toEqual([true]);
  });
});

describe('Anbar', () => {
  it('puts the running-low item in the warning tone and lists the movements', async () => {
    setUp([Permissions.inventoryView]);
    const fixture = TestBed.createComponent(InventoryComponent);
    fixture.detectChanges();

    backend.expectOne('/api/admin/kiosks').flush([kiosk]);
    backend.expectOne((request) => request.url === '/api/admin/inventory/items').flush([
      { id: 'p', sku: 'A4-500', name: 'A4 kağız', type: 'PaperA4Ream', compatiblePrinterModels: [], unit: 'bağlama', minStock: 5, stock: 2, location: 'Rəf 1', isActive: true, isLow: true },
    ]);
    backend.expectOne((request) => request.url === '/api/admin/inventory/movements').flush(
      page([{ id: 'm', itemId: 'p', itemName: 'A4 kağız', type: 'UsedAtKiosk', quantity: -1, stockAfter: 2, kioskId, at: '2026-10-16T09:00:00Z' }]),
    );
    await settle(fixture);

    expect(text(fixture)).toContain('ops.inventory.low');
    expect(text(fixture), 'nothing to change without the permission').not.toContain('ops.inventory.move');
  });

  it('records a movement only when it makes sense', () => {
    setUp([Permissions.inventoryManage], {
      extra: [
        { provide: MAT_DIALOG_DATA, useValue: { item: { id: 'p', name: 'A4', stock: 2, unit: 'bağlama' }, kiosks: [kiosk] } },
        { provide: MatDialogRef, useValue: { close: () => undefined } },
      ],
    });
    const dialog = TestBed.createComponent(InventoryMovementDialogComponent).componentInstance;

    dialog.quantity.set(-2);
    expect(dialog.valid(), 'a purchase is never negative').toBe(false);
    dialog.type.set('Adjustment');
    expect(dialog.valid(), 'a correction may go down').toBe(true);
    dialog.quantity.set(0);
    expect(dialog.valid()).toBe(false);
    dialog.type.set('UsedAtKiosk');
    dialog.quantity.set(1);
    expect(dialog.valid(), 'used where?').toBe(false);
    dialog.kioskId.set(kioskId);
    expect(dialog.valid()).toBe(true);
  });
});

describe('Xəbərdarlıq qaydaları', () => {
  it('says nothing is low, lists where each rule works, and deletes one after asking', async () => {
    setUp([Permissions.suppliesView, Permissions.supplyRulesManage]);
    const fixture = TestBed.createComponent(SupplyRulesComponent);
    fixture.detectChanges();

    backend.expectOne('/api/admin/kiosks').flush([kiosk]);
    const rule = (id: string, isGlobal: boolean, kioskIds: string[]) => ({
      id, name: id, isGlobal, kioskIds, resource: 'PaperA4', resourceVariant: null, unit: 'Sheets',
      warningThreshold: 200, criticalThreshold: 50, channels: ['AdminPanel'], createServiceTask: true, taskPriority: 'Normal', isActive: true,
    });
    backend.expectOne('/api/admin/supply-rules').flush([rule('Ümumi A4', true, []), rule('Kitabxana', false, [kioskId])]);
    await new Promise((r) => setTimeout(r));
    backend.expectOne((request) => request.url === '/api/admin/supply-alerts').flush(page([]));
    await settle(fixture);

    expect(text(fixture)).toContain('ops.rules.noAlerts');
    expect(text(fixture)).toContain('KSK-9002');
    expect(text(fixture), 'the global rule says so').toContain('monitoring.rules.global');

    void fixture.componentInstance.remove(rule('Kitabxana', false, [kioskId]) as never);
    await new Promise((r) => setTimeout(r));
    backend.expectOne('/api/admin/supply-rules/Kitabxana').flush(null);
    await new Promise((r) => setTimeout(r));
    backend.expectOne('/api/admin/supply-rules').flush([rule('Ümumi A4', true, [])]);
    await new Promise((r) => setTimeout(r));
    backend.expectOne((request) => request.url === '/api/admin/supply-alerts').flush(page([]));
  });
});

describe('Audit log', () => {
  it('compares two JSON objects field by field and leaves plain values as they are', () => {
    expect(diff('{"status":"Online","name":"A"}', '{"status":"Maintenance","name":"A"}')).toEqual([
      { field: 'name', before: 'A', after: 'A', changed: false },
      { field: 'status', before: 'Online', after: 'Maintenance', changed: true },
    ]);
    expect(diff(null, '{"x":1}')).toEqual([{ field: 'x', before: '', after: '1', changed: true }]);
    expect(diff('c0ffee', null), 'an id is not an object').toBeNull();
    expect(diff(null, null)).toBeNull();
  });

  it('asks with the filter and shows an entry\'s change when it is opened', async () => {
    setUp([Permissions.auditView]);
    const fixture = TestBed.createComponent(AuditLogComponent);
    fixture.detectChanges();

    backend.expectOne((request) => request.url === '/api/admin/audit-logs').flush(
      page([
        { id: 'e-1', at: '2026-10-16T09:00:00Z', actorType: 'Admin', adminName: 'Test SuperAdmin', action: 'kiosks.update', entityType: 'Kiosk', entityId: kioskId, before: '{"name":"A"}', after: '{"name":"B"}' },
        { id: 'e-2', at: '2026-10-16T08:00:00Z', actorType: 'Kiosk', action: 'kiosks.command.Pause.Succeeded', entityType: 'Kiosk', entityId: kioskId, before: 'c1' },
      ]),
    );
    await settle(fixture);

    expect(text(fixture)).toContain('Test SuperAdmin');
    expect(text(fixture)).toContain('jobs.actor.Kiosk');

    fixture.componentInstance.toggle(fixture.componentInstance.rows()![0]);
    fixture.detectChanges();
    const changed = (fixture.nativeElement as HTMLElement).querySelector('.ops__changed');
    expect(changed?.textContent).toContain('A');
    expect(changed?.textContent).toContain('B');

    fixture.componentInstance.action.set('kiosks.');
    fixture.componentInstance.apply();
    const filtered = backend.expectOne((request) => request.url === '/api/admin/audit-logs');
    expect(filtered.request.params.get('action')).toBe('kiosks.');
    filtered.flush(page([]));
    await settle(fixture);

    expect(text(fixture)).toContain('ops.audit.noMatch');
  });
});
