import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { KioskPrinters, KioskSupplies, QueueEntry } from '../../core/api/step3.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { KioskPrintersComponent } from './kiosk-printers.component';
import { KioskQueueComponent } from './kiosk-queue.component';
import { KioskSuppliesComponent } from './kiosk-supplies.component';

/** Stands in for the SignalR connection: the test fires the events itself. */
class FakeHub {
  readonly handlers = new Map<string, (payload: unknown) => void>();

  // The page also asks about the connection itself (liveRefresh): up, and staying up.
  connectionState = (): 'connected' => 'connected';
  onState = (): (() => void) => () => undefined;
  onReconnect = (): (() => void) => () => undefined;

  on<T>(event: string, handler: (payload: T) => void): () => void {
    this.handlers.set(event, handler as (payload: unknown) => void);
    return () => this.handlers.delete(event);
  }

  fire(event: string, payload: unknown): void {
    this.handlers.get(event)?.(payload);
  }
}

const kioskId = '11111111-1111-1111-1111-111111111111';

function allow(...permissions: string[]) {
  return {
    can: (permission: string) => permissions.includes(permission),
    canAny: (list: readonly string[]) => list.some((permission) => permissions.includes(permission)),
  };
}

function setUp(permissions: string[]): { backend: HttpTestingController; hub: FakeHub } {
  const hub = new FakeHub();

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      { provide: AdminHubService, useValue: hub },
      { provide: AuthService, useValue: allow(...permissions) },
    ],
  });

  return { backend: TestBed.inject(HttpTestingController), hub };
}

const printers: KioskPrinters = {
  kioskId,
  status: 'Online',
  selectedPrinter: 'HP LaserJet',
  printers: [
    {
      name: 'HP LaserJet',
      driverName: 'HP Universal',
      isOnline: true,
      detectedCapabilities: { color: true, duplex: true, a3: false },
    },
  ],
  detected: { color: true, duplex: true, a3: false },
  overrides: { color: null, duplex: null, a3: null },
  effective: { color: true, duplex: true, a3: false },
};

describe('KioskPrintersComponent', () => {
  let backend: HttpTestingController;
  let hub: FakeHub;

  beforeEach(() => ({ backend, hub } = setUp([Permissions.printerCapabilitiesManage, Permissions.printersTestPrint])));

  afterEach(() => backend.verify());

  async function load() {
    const fixture = TestBed.createComponent(KioskPrintersComponent);
    fixture.componentRef.setInput('kioskId', kioskId);
    fixture.detectChanges();

    backend.expectOne(`/api/admin/kiosks/${kioskId}/printers`).flush(printers);
    await fixture.whenStable();
    fixture.detectChanges();

    return fixture;
  }

  it('only switches capabilities off: "follow the printer" is sent as null', async () => {
    const component = (await load()).componentInstance;

    expect(component.changed()).toBe(false);

    component.choose('color', 'off');
    expect(component.changed()).toBe(true);

    const saving = component.saveCapabilities();
    const request = backend.expectOne(`/api/admin/kiosks/${kioskId}/capabilities`);
    expect(request.request.body).toEqual({ color: false, duplex: null, a3: null });

    request.flush({ ...printers, overrides: { color: false, duplex: null, a3: null } });
    await saving;

    expect(component.choices().color).toBe('off');
    expect(component.changed()).toBe(false);
  });

  it('opens each printer on its own page; a passed test print of this kiosk asks again and tells the page', async () => {
    const fixture = await load();
    const link = (fixture.nativeElement as HTMLElement).querySelector('td a') as HTMLAnchorElement;

    expect(link.textContent?.trim()).toBe('HP LaserJet');
    expect(link.getAttribute('href')).toBe(`/kiosks/${kioskId}/printers/HP%20LaserJet`);

    // The commands are listed on the "Əmrlər" tab now, not here (T-212).
    expect((fixture.nativeElement as HTMLElement).querySelectorAll('table').length).toBe(1);

    const statuses: string[] = [];
    fixture.componentInstance.statusChange.subscribe((status) => statuses.push(status));
    const command = {
      id: 'c1',
      kioskId,
      type: 'TestPrint',
      status: 'Succeeded',
      result: null,
      createdAt: '2026-09-28T10:00:00Z',
      completedAt: '2026-09-28T10:00:05Z',
    };

    hub.fire('CommandResult', { ...command, kioskId: 'another' });
    backend.expectNone(`/api/admin/kiosks/${kioskId}/printers`);

    hub.fire('CommandResult', command);
    backend.expectOne(`/api/admin/kiosks/${kioskId}/printers`).flush(printers);
    await fixture.whenStable();
    expect(statuses).toEqual(['Online']);
  });
});

describe('KioskQueueComponent', () => {
  let backend: HttpTestingController;
  let hub: FakeHub;

  beforeEach(() => ({ backend, hub } = setUp([Permissions.jobsView])));

  afterEach(() => backend.verify());

  it('follows the queue the kiosk shows, and ignores other kiosks', async () => {
    const fixture = TestBed.createComponent(KioskQueueComponent);
    fixture.componentRef.setInput('kioskId', kioskId);
    fixture.detectChanges();

    backend.expectOne(`/api/admin/kiosks/${kioskId}/queue`).flush([]);
    await fixture.whenStable();

    const entry: QueueEntry = {
      jobId: 'j1',
      jobCode: 'A1B2C3',
      position: 1,
      status: 'Queued',
      pages: 3,
      copies: 2,
      userInitial: null,
      fileName: 'report.pdf',
      queuedAt: '2026-09-28T10:00:00Z',
    };

    hub.fire('QueueUpdated', { kioskId: 'another', queue: [entry] });
    expect(fixture.componentInstance.queue()).toEqual([]);

    hub.fire('QueueUpdated', { kioskId, queue: [entry] });
    expect(fixture.componentInstance.queue()).toEqual([entry]);
  });
});

describe('KioskSuppliesComponent', () => {
  let backend: HttpTestingController;

  const supplies: KioskSupplies = {
    paper: {
      A4: { capacity: 500, current: 420, lastRefillAt: null },
      A3: { capacity: 0, current: 0, lastRefillAt: null },
    },
    events: [],
  };

  beforeEach(() => ({ backend } = setUp([Permissions.suppliesView, Permissions.paperRefill])));

  // The rules under the counters are asked for on their own (T-212); tests that are not about them answer "none".
  afterEach(() => {
    backend.match('/api/admin/supply-rules').forEach((request) => request.flush([]));
    backend.verify();
  });

  async function load() {
    const fixture = TestBed.createComponent(KioskSuppliesComponent);
    fixture.componentRef.setInput('kioskId', kioskId);
    fixture.detectChanges();

    backend.expectOne(`/api/admin/kiosks/${kioskId}/supplies`).flush(supplies);
    await fixture.whenStable();

    return fixture.componentInstance;
  }

  it('offers only the trays that have a capacity', async () => {
    const component = await load();

    expect(component.refillable()).toEqual(['A4']);
    expect(component.resourceKey('PaperA4')).toBe('supplies.paperA4');
    expect(component.variantOf('PaperA4')).toBe('');

    // A changed cartridge: the kind is ours to translate, the name is the printer's.
    expect(component.resourceKey('Toner:Black Cartridge')).toBe('supplies.toner');
    expect(component.variantOf('Toner:Black Cartridge')).toBe(' (Black Cartridge)');
  });

  // T-258: a newly installed kiosk's nought is a count nobody made.
  it('asks for the counter to be set instead of calling an uncounted tray empty', async () => {
    const fixture = TestBed.createComponent(KioskSuppliesComponent);
    fixture.componentRef.setInput('kioskId', kioskId);
    fixture.detectChanges();

    backend.expectOne(`/api/admin/kiosks/${kioskId}/supplies`).flush({
      ...supplies,
      paper: { ...supplies.paper, A4: { capacity: 500, current: 0, lastRefillAt: null, counted: false } },
    });
    await fixture.whenStable();
    fixture.detectChanges();

    const tray = (): string =>
      (fixture.nativeElement as HTMLElement).querySelector('.supplies__tray')?.textContent ?? '';

    expect(tray()).toContain('supplies.counterUnset');
    expect(tray()).toContain('supplies.counterUnsetHint');
    expect(tray(), 'not "out of paper"').not.toContain('supplies.empty');
    expect(tray(), 'no 0 / 500').not.toContain('supplies.level');

    // The first refill is recorded: from then on it is a count like any other.
    const component = fixture.componentInstance;
    component.mode.set('Exact');
    component.sheets.set(0);

    const refilling = component.refill();
    backend
      .expectOne(`/api/admin/kiosks/${kioskId}/paper/refill`)
      .flush({
        ...supplies,
        paper: {
          ...supplies.paper,
          A4: { capacity: 500, current: 0, lastRefillAt: '2026-10-13T09:00:00Z', counted: true },
        },
      });
    await refilling;
    fixture.detectChanges();

    expect(tray()).not.toContain('supplies.counterUnset');
    expect(tray()).toContain('supplies.empty');
  });

  // T-212: what the printer says of its toner, when it runs out, and the rules at work.
  it('shows the toner under its alert, the forecast and the rules that apply to this kiosk', async () => {
    const fixture = TestBed.createComponent(KioskSuppliesComponent);
    fixture.componentRef.setInput('kioskId', kioskId);
    fixture.componentRef.setInput('kioskCode', 'KSK-9002');
    fixture.detectChanges();

    backend.expectOne(`/api/admin/kiosks/${kioskId}/supplies`).flush({
      ...supplies,
      levels: [{ resource: 'Toner', variant: 'Black', name: 'Black Cartridge', percent: 7, readAt: '2026-10-15T09:00:00Z' }],
      forecasts: [{ resource: 'PaperA4', variant: null, unit: 'Sheets', current: 420, perDay: 60, runsOutAt: '2026-10-22T09:00:00Z' }],
      alerts: [
        { id: 'a1', kioskId, resource: 'Toner', variant: 'Black', unit: 'Percent', level: 'Warning', value: 7, raisedAt: '2026-10-15T08:00:00Z' },
      ],
    });
    const rule = (id: string, isGlobal: boolean, kioskIds: string[]) => ({
      id,
      name: id,
      isGlobal,
      kioskIds,
      resource: 'Toner',
      resourceVariant: null,
      unit: 'Percent',
      warningThreshold: 15,
      criticalThreshold: 3,
      channels: ['AdminPanel'],
      createServiceTask: true,
      taskPriority: 'Normal',
      isActive: true,
    });
    backend
      .expectOne('/api/admin/supply-rules')
      .flush([rule('global', true, []), rule('mine', false, [kioskId]), rule('theirs', false, ['other-kiosk'])]);
    await fixture.whenStable();
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    const toner = [...page.querySelectorAll('.supplies__tray')].find((card) => card.textContent?.includes('Black Cartridge'));

    expect(toner?.textContent).toContain('7%');
    expect(toner?.querySelector('.supplies__fill--low'), 'the bar is in the warning colour').not.toBeNull();
    expect(toner?.textContent).toContain('monitoring.alertLevel.Warning');

    expect(page.textContent).toContain('monitoring.forecast.title');
    expect(page.textContent).not.toContain('monitoring.forecast.empty');

    // The global rule and this kiosk's own; another kiosk's rule is not shown.
    const rules = [...page.querySelectorAll('table')].find((table) => table.textContent?.includes('monitoring.rules.colSource'));
    const rows = rules?.querySelectorAll('tbody tr, tr.mat-mdc-row') ?? [];
    expect(rows.length).toBe(2);
    expect(rules?.textContent).toContain('monitoring.rules.global');
    expect(rules?.textContent).toContain('monitoring.rules.thisKiosk');
  });

  it('checks the count the way the server does', async () => {
    const component = await load();

    // Adding may fill the tray only up to its capacity: 80 sheets fit.
    component.mode.set('Add');
    component.sheets.set(80);
    expect(component.sheetsValid()).toBe(true);
    component.sheets.set(81);
    expect(component.sheetsValid()).toBe(false);
    component.sheets.set(0);
    expect(component.sheetsValid()).toBe(false);

    // A recount names what is in the tray, empty included.
    component.mode.set('Exact');
    component.sheets.set(0);
    expect(component.sheetsValid()).toBe(true);
    component.sheets.set(501);
    expect(component.sheetsValid()).toBe(false);

    // A full tray needs no count.
    component.mode.set('Full');
    component.sheets.set(null);
    expect(component.sheetsValid()).toBe(true);
  });

  it('records a refill once, under an idempotency key', async () => {
    const component = await load();

    component.mode.set('Add');
    component.sheets.set(50);

    const refilling = component.refill();
    const request = backend.expectOne(`/api/admin/kiosks/${kioskId}/paper/refill`);

    expect(request.request.headers.has('Idempotency-Key')).toBe(true);
    expect(request.request.body).toEqual({ paper: 'A4', mode: 'Add', sheets: 50 });

    request.flush({ ...supplies, paper: { ...supplies.paper, A4: { capacity: 500, current: 470, lastRefillAt: null } } });
    await refilling;

    expect(component.trays()[0].stock.current).toBe(470);
    expect(component.sheets()).toBeNull();
  });
});
