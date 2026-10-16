import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { KioskTelemetry, PrinterReading, PrinterSample } from '../../core/api/step7.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { KioskLiveStore } from '../../core/realtime/kiosk-live.store';
import { TelemetryLiveStore } from '../../core/realtime/telemetry-live.store';
import { KioskCommandsComponent } from '../kiosks/kiosk-commands.component';
import { KioskIncidentsComponent } from '../kiosks/kiosk-incidents.component';
import { KioskStatusComponent } from '../kiosks/kiosk-status.component';
import { KioskTasksComponent } from '../kiosks/kiosk-tasks.component';
import { PrinterDetailComponent } from './printer-detail.component';
import { PrintersComponent } from './printers.component';

/** Stands in for the SignalR connection: every subscriber hears what the test fires. */
class FakeHub {
  private readonly handlers = new Map<string, Set<(payload: unknown) => void>>();

  connectionState = (): 'connected' => 'connected';
  onState = (): (() => void) => () => undefined;
  onReconnect = (): (() => void) => () => undefined;

  on<T>(event: string, handler: (payload: T) => void): () => void {
    const set = this.handlers.get(event) ?? new Set();
    set.add(handler as (payload: unknown) => void);
    this.handlers.set(event, set);

    return () => set.delete(handler as (payload: unknown) => void);
  }

  fire(event: string, payload: unknown): void {
    [...(this.handlers.get(event) ?? [])].forEach((handler) => handler(payload));
  }
}

const kioskId = '11111111-1111-1111-1111-111111111111';
const otherKiosk = '22222222-2222-2222-2222-222222222222';

let backend: HttpTestingController;
let hub: FakeHub;
let account: ReturnType<typeof signal<{ id: string } | null>>;

function setUp(permissions: string[], confirm = true): void {
  hub = new FakeHub();
  account = signal<{ id: string } | null>({ id: 'admin-1' });

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
          admin: account,
          can: (permission: string) => permissions.includes(permission),
          canAny: (list: readonly string[]) => list.some((permission) => permissions.includes(permission)),
        },
      },
      { provide: MatDialog, useValue: { open: () => ({ afterClosed: () => of(confirm ? { reason: '' } : undefined) }) } },
    ],
  });

  backend = TestBed.inject(HttpTestingController);
}

function text(fixture: ComponentFixture<unknown>): string {
  return (fixture.nativeElement as HTMLElement).textContent ?? '';
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await fixture.whenStable();
  fixture.detectChanges();
}

function telemetry(overrides: Partial<KioskTelemetry['sample']> = {}, stale = false): KioskTelemetry {
  return {
    kioskId,
    stale,
    intervalSeconds: 30,
    status: { status: 'Online', lastHeartbeatAt: null, openIncidents: 0, paper: {} },
    sample: {
      timestamp: new Date().toISOString(),
      app: { version: '0.7.0' },
      system: { cpuPercent: 12.4, memoryUsedBytes: 4 * 1024 ** 3, memoryTotalBytes: 8 * 1024 ** 3, systemDiskFreeBytes: 50 * 1024 ** 3 },
      network: { backendLatencyMs: 80, hubConnected: true },
      breaches: {},
      ...overrides,
    },
  };
}

function sample(name: string, extra: Partial<PrinterSample> = {}): PrinterSample {
  return {
    timestamp: '2026-10-15T09:00:00Z',
    printer: { kioskId, printerName: name },
    selected: false,
    manufacturer: 'HP',
    model: 'LaserJet M404',
    state: 'idle',
    stateReasons: [],
    online: true,
    supplies: [{ name: 'Black Cartridge', type: 'toner', levelPercent: 40 }],
    trays: [],
    lifetimePages: 48_213,
    spoolerJobs: [],
    ...extra,
  };
}

function reading(name: string, extra: Partial<PrinterSample> = {}, kiosk = kioskId, stale = false): PrinterReading {
  return { kioskId: kiosk, sample: { ...sample(name, extra), printer: { kioskId: kiosk, printerName: name } }, stale };
}

afterEach(() => backend.verify());

describe('Kiosk detail → Cari vəziyyət', () => {
  beforeEach(() => setUp([Permissions.kiosksView]));

  async function open(answer: KioskTelemetry) {
    const fixture = TestBed.createComponent(KioskStatusComponent);
    fixture.componentRef.setInput('kioskId', kioskId);
    fixture.detectChanges();

    backend.expectOne(`/api/admin/kiosks/${kioskId}/telemetry`).flush(answer);
    backend.expectOne((request) => request.url === `/api/admin/kiosks/${kioskId}/telemetry/history`).flush([]);
    await settle(fixture);

    return fixture;
  }

  it('shows the report, says "not reported" rather than nought, and follows the next report live', async () => {
    const fixture = await open(telemetry());

    expect(text(fixture)).toContain('0.7.0');
    expect(text(fixture)).toContain('12%');
    expect(text(fixture), 'nothing about the flash drive was reported').toContain('monitoring.notReported');
    expect(text(fixture)).toContain('monitoring.health.disk.ok');

    // The next report arrives over the hub: the page shows it without asking.
    hub.fire('KioskTelemetryUpdated', telemetry({
      system: { cpuPercent: 97, systemDiskFreeBytes: 1024 ** 3 },
      breaches: { disk: '2026-10-15T09:00:00Z' },
    }));
    fixture.detectChanges();

    expect(text(fixture)).toContain('97%');
    expect(text(fixture)).toContain('monitoring.health.disk.breached');
    expect(text(fixture)).toContain('monitoring.breach.disk');
    expect((fixture.nativeElement as HTMLElement).querySelector('.neo-metric--warning')).not.toBeNull();

    // Another kiosk's report changes nothing here.
    hub.fire('KioskTelemetryUpdated', { ...telemetry({ system: { cpuPercent: 3 } }), kioskId: otherKiosk });
    fixture.detectChanges();
    expect(text(fixture)).toContain('97%');
  });

  it('says the figures are old when the kiosk stopped reporting, and says so when it never did', async () => {
    const stale = await open(telemetry({}, true));
    expect(text(stale)).toContain('monitoring.status.stale');
    expect(text(stale), 'no health chips over old figures').not.toContain('monitoring.health.disk.ok');
  });

  it('has an empty state for a kiosk that never reported', async () => {
    const fixture = await open({ ...telemetry(), sample: null });
    expect(text(fixture)).toContain('monitoring.status.never');
  });
});

describe('Kiosk detail → Əmrlər', () => {
  async function open(status: string) {
    TestBed.inject(KioskLiveStore).learn(kioskId, status as never);
    const fixture = TestBed.createComponent(KioskCommandsComponent);
    fixture.componentRef.setInput('kioskId', kioskId);
    fixture.detectChanges();

    backend.expectOne(`/api/admin/kiosks/${kioskId}/commands`).flush([]);
    await settle(fixture);

    return fixture;
  }

  const command = {
    id: 'c1',
    kioskId,
    type: 'Pause',
    status: 'Sent',
    result: null,
    createdAt: '2026-10-15T09:00:00Z',
    completedAt: null,
  };

  it('pauses after asking, and shows the answer the kiosk sends', async () => {
    setUp([Permissions.kiosksView, Permissions.kioskCommandsPause, Permissions.kioskCommandsRestart]);
    const fixture = await open('Online');
    const buttons = (): string[] =>
      [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')].map((button) => button.textContent?.trim() ?? '');

    expect(buttons().some((label) => label.includes('monitoring.commands.pause'))).toBe(true);
    expect(buttons().some((label) => label.includes('monitoring.commands.reboot')), 'no reboot without its permission').toBe(false);
    expect(text(fixture)).toContain('printers.commandsEmpty');

    const sending = fixture.componentInstance.ask('pause');
    await Promise.resolve();
    backend.expectOne(`/api/admin/kiosks/${kioskId}/pause`).flush(command);
    await sending;
    await settle(fixture);

    expect(text(fixture)).toContain('printers.commandStatus.Sent');
    // The kiosk is in maintenance now: the same button resumes it.
    expect(buttons().some((label) => label.includes('monitoring.commands.resume'))).toBe(true);

    hub.fire('CommandResult', { ...command, status: 'Succeeded', completedAt: '2026-10-15T09:00:03Z' });
    fixture.detectChanges();

    expect(text(fixture)).toContain('printers.commandStatus.Succeeded');
    expect(text(fixture)).not.toContain('printers.commandStatus.Sent');
  });

  it('sends nothing when the question is cancelled', async () => {
    setUp([Permissions.kiosksView, Permissions.kioskCommandsReboot], false);
    const fixture = await open('Online');

    await fixture.componentInstance.ask('reboot');
    backend.expectNone(`/api/admin/kiosks/${kioskId}/reboot`);
  });
});

describe('Kiosk detail → Servis tapşırıqları və Incident-lər', () => {
  beforeEach(() => setUp([Permissions.kiosksView, Permissions.serviceTasksView, Permissions.incidentsView]));

  it("lists the kiosk's open tasks and fetches them again when one of its tasks changes", async () => {
    const fixture = TestBed.createComponent(KioskTasksComponent);
    fixture.componentRef.setInput('kioskId', kioskId);
    fixture.detectChanges();

    const first = backend.expectOne((request) => request.url === '/api/admin/service-tasks');
    expect(first.request.params.get('kioskId')).toBe(kioskId);
    expect(first.request.params.getAll('status')).toEqual(['Open', 'Assigned', 'InProgress']);
    first.flush({
      items: [
        { id: 't1', kioskId, type: 'RefillPaper', priority: 'Critical', status: 'Open', source: 'AlertRule', createdAt: '2026-10-15T09:00:00Z' },
      ],
      total: 1,
      page: 1,
      pageSize: 50,
    });
    await settle(fixture);

    expect(text(fixture)).toContain('operator.taskType.RefillPaper');
    expect(text(fixture)).toContain('monitoring.tasks.unassigned');

    // Another kiosk's task is not this tab's business.
    hub.fire('ServiceTaskUpdated', { id: 't9', kioskId: otherKiosk });
    await new Promise((resolve) => setTimeout(resolve, 900));
    backend.expectNone((request) => request.url === '/api/admin/service-tasks');

    hub.fire('ServiceTaskUpdated', { id: 't1', kioskId });
    await new Promise((resolve) => setTimeout(resolve, 900));
    backend.expectOne((request) => request.url === '/api/admin/service-tasks').flush({ items: [], total: 0, page: 1, pageSize: 50 });
    await settle(fixture);

    expect(text(fixture)).toContain('monitoring.tasks.noneOpen');
  });

  it("lists the kiosk's incidents with their severity by the panel's own names", async () => {
    const fixture = TestBed.createComponent(KioskIncidentsComponent);
    fixture.componentRef.setInput('kioskId', kioskId);
    fixture.detectChanges();

    backend.expectOne((request) => request.url === '/api/admin/incidents').flush({
      items: [{ id: 'i1', type: 'DiskLow', severity: 'Medium', status: 'Open', details: 'Disk is low', kioskId, createdAt: '2026-10-15T09:00:00Z' }],
      total: 1,
      page: 1,
      pageSize: 50,
    });
    await settle(fixture);

    expect(text(fixture)).toContain('money.incidentSeverity.Medium');
    expect(text(fixture)).toContain('Disk is low');
    expect(text(fixture)).toContain('monitoring.incidentStatus.Open');
  });
});

describe('Printerlər (ümumi)', () => {
  beforeEach(() => setUp([Permissions.printersView, Permissions.suppliesView, Permissions.kiosksView]));

  async function open() {
    const fixture = TestBed.createComponent(PrintersComponent);
    fixture.detectChanges();

    backend.expectOne('/api/admin/kiosks').flush([
      { id: kioskId, code: 'KSK-9002', name: 'One', status: 'Online' },
      { id: otherKiosk, code: 'KSK-9003', name: 'Two', status: 'Online' },
    ]);
    backend.expectOne('/api/admin/printers').flush([
      reading('HP', { selected: true }),
      reading('Spare', { online: false }),
      reading('Canon', { selected: true, state: 'stopped', stateReasons: ['media-jam-error'] }, otherKiosk),
    ]);
    // The alerts are asked for once the printers are in.
    await new Promise((resolve) => setTimeout(resolve));
    backend.expectOne((request) => request.url === '/api/admin/supply-alerts').flush({
      items: [{ id: 'a1', kioskId, resource: 'Toner', variant: null, unit: 'Percent', level: 'Critical', value: 2, raisedAt: '' }],
      total: 1,
      page: 1,
      pageSize: 500,
    });
    await settle(fixture);

    return fixture;
  }

  it('lists every printer with its status and counts each filter', async () => {
    const fixture = await open();
    const rows = (): HTMLElement[] => [...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('tr.mat-mdc-row')];

    expect(rows().length).toBe(3);
    expect(text(fixture)).toContain('monitoring.printerState.ready');
    expect(text(fixture)).toContain('monitoring.printerState.unreachable');
    expect(text(fixture)).toContain('monitoring.printerState.stopped');
    expect(fixture.componentInstance.counts()).toEqual({ all: 3, lowToner: 1, faulty: 1, unreachable: 1 });

    fixture.componentInstance.filter.set('lowToner');
    fixture.detectChanges();
    expect(rows().length).toBe(1);
    expect(rows()[0].textContent).toContain('KSK-9002');
    expect(rows()[0].querySelector('.printers__tone--danger'), 'a critical toner alert is in the danger colour').not.toBeNull();

    fixture.componentInstance.filter.set('unreachable');
    fixture.detectChanges();
    expect(rows().map((row) => row.textContent)).toEqual([expect.stringContaining('Spare')]);
  });

  it('follows a printer that reports again, without asking the server', async () => {
    const fixture = await open();

    hub.fire('PrinterTelemetryUpdated', { kioskId, printers: [reading('Spare', { online: true, state: 'printing' })] });
    fixture.detectChanges();

    expect(fixture.componentInstance.counts().unreachable).toBe(0);
    expect(text(fixture)).toContain('monitoring.printerState.printing');
    expect(fixture.componentInstance.counts().all, 'the printer the event did not name stays').toBe(3);
  });
});

describe('Printer detalı', () => {
  beforeEach(() => setUp([Permissions.printersView, Permissions.printersTestPrint, Permissions.printersSelect, Permissions.kiosksView]));

  async function open(name: string) {
    const fixture = TestBed.createComponent(PrinterDetailComponent);
    fixture.componentRef.setInput('id', kioskId);
    fixture.componentRef.setInput('name', name);
    fixture.detectChanges();

    backend.expectOne('/api/admin/kiosks').flush([{ id: kioskId, code: 'KSK-9002', name: 'One', status: 'Online' }]);
    backend.expectOne(`/api/admin/kiosks/${kioskId}/printers/telemetry`).flush([
      reading('HP', {
        selected: true,
        serialNumber: 'VNB3R12345',
        spoolerJobs: [{ id: '17', sizeBytes: 2048, state: 'pending', submittedAt: '2026-10-15T08:50:00Z' }],
      }),
      reading('Spare'),
    ]);
    backend.expectOne((request) => request.url.endsWith('/printers/telemetry/history')).flush([]);
    backend.expectOne((request) => request.url.endsWith('/printers/telemetry/events')).flush([
      { at: '2026-10-14T09:00:00Z', kind: 'SupplyReplaced', subject: 'Black Cartridge', fromLevel: 3, toLevel: 100 },
    ]);
    await settle(fixture);

    return fixture;
  }

  it('shows who the printer is, a stuck spooler job and its history; the selected one offers the test page', async () => {
    const fixture = await open('HP');

    expect(text(fixture)).toContain('VNB3R12345');
    expect(text(fixture), 'firmware was not reported').toContain('monitoring.printerNotReported');
    expect(text(fixture)).toContain('monitoring.printer.stuck');
    expect(text(fixture)).toContain('monitoring.printerEvent.SupplyReplaced');
    expect(text(fixture)).toContain('3% → 100%');
    expect(text(fixture)).toContain('printers.testPrint');
    const buttons = [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')].map((button) => button.textContent?.trim());
    expect(buttons, 'the selected printer is not offered as the default again').not.toContain('printers.select');

    // A live reading of this printer changes the page.
    hub.fire('PrinterTelemetryUpdated', { kioskId, printers: [reading('HP', { selected: true, online: false })] });
    fixture.detectChanges();
    expect(text(fixture)).toContain('monitoring.printerState.unreachable');
  });

  it('says so when the kiosk has no such printer', async () => {
    const fixture = await open('Gone');
    expect(text(fixture)).toContain('monitoring.printer.notFound');
  });
});

describe('TelemetryLiveStore', () => {
  beforeEach(() => setUp([Permissions.printersView]));

  it('forgets what the last account saw when another signs in', async () => {
    const store = TestBed.inject(TelemetryLiveStore);
    hub.fire('KioskTelemetryUpdated', telemetry());
    expect(store.telemetryOf(kioskId)).toBeDefined();

    account.set({ id: 'admin-2' });
    TestBed.tick();

    expect(store.telemetryOf(kioskId)).toBeUndefined();
  });
});
