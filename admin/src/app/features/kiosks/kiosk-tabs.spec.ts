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
    backend.expectOne(`/api/admin/kiosks/${kioskId}/commands`).flush([]);
    await fixture.whenStable();

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

  it('puts a command result from the hub at the top, for this kiosk only', async () => {
    const fixture = await load();
    const component = fixture.componentInstance;
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
    expect(component.commands()).toEqual([]);

    const statuses: string[] = [];
    fixture.componentInstance.statusChange.subscribe((status) => statuses.push(status));

    hub.fire('CommandResult', command);
    expect(component.commands()).toEqual([command]);

    // A passed test print takes the kiosk into service: the tab asks again and tells the page.
    backend.expectOne(`/api/admin/kiosks/${kioskId}/printers`).flush(printers);
    backend.expectOne(`/api/admin/kiosks/${kioskId}/commands`).flush([command]);
    await fixture.whenStable();
    expect(statuses).toEqual(['Online']);

    // The same command again replaces the row rather than adding one.
    hub.fire('CommandResult', { ...command, status: 'Failed' });
    expect(component.commands().map((item) => item.status)).toEqual(['Failed']);
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

  afterEach(() => backend.verify());

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
