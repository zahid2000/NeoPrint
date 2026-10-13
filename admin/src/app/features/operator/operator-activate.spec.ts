import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { DeviceDetails } from '../../core/api/admin-api.service';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { NotifyService } from '../../shared/notify.service';
import { OperatorActivateComponent } from './operator-activate.component';
import { OperatorKioskCard } from './operator.models';
import { activationQr } from './qr-scanner';

function device(overrides: Partial<DeviceDetails> = {}): DeviceDetails {
  return {
    deviceId: 'd-1',
    status: 'Pending',
    platform: 'Linux',
    appVersion: '0.7.0',
    fingerprint: {
      hostname: 'kiosk-new',
      os: 'Linux',
      osVersion: null,
      machineId: null,
      macAddresses: null,
      cpu: null,
      ramGb: 8,
      screenResolution: null,
    },
    printers: [{ name: 'HP LaserJet', driverName: null, isOnline: true, color: false, duplex: true, a3: false }],
    lastIp: null,
    registeredAt: '2026-10-14T08:00:00Z',
    lastSeenAt: '2026-10-14T08:00:00Z',
    kioskId: null,
    kioskCode: null,
    ...overrides,
  } as DeviceDetails;
}

function kiosk(overrides: Partial<OperatorKioskCard> = {}): OperatorKioskCard {
  return {
    id: 'k-1',
    code: 'KSK-0001',
    name: 'Library',
    address: 'Main street 1',
    status: 'Online',
    paper: {},
    levels: [],
    forecasts: [],
    alerts: [],
    queueLength: 0,
    openIncidents: 0,
    openTasks: 0,
    attention: 0,
    ...overrides,
  };
}

/** "Kiosk aktivləşdir" (plan section 8B, T-265): the code off the screen, the operator's own kiosk, one request. */
describe('OperatorActivateComponent', () => {
  let fixture: ComponentFixture<OperatorActivateComponent>;
  let page: OperatorActivateComponent;
  let backend: HttpTestingController;
  let notify: { success: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn> };

  const text = (): string => (fixture.nativeElement as HTMLElement).textContent ?? '';

  function setUp(...permissions: string[]): void {
    notify = { success: vi.fn(), error: vi.fn() };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        provideTranslateService(),
        { provide: NotifyService, useValue: notify },
        { provide: AuthService, useValue: { can: (permission: string) => permissions.includes(permission) } },
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(OperatorActivateComponent);
    page = fixture.componentInstance;
    fixture.detectChanges();
  }

  /** Types the code, presses "Cihazı tap", and answers the two requests that follow. */
  async function found(kiosks: OperatorKioskCard[], details = device()): Promise<void> {
    page.code.set('abcd-2345');

    const looking = page.find();

    const lookup = backend.expectOne('/api/admin/devices/lookup');
    expect(lookup.request.body, 'as the screen shows it').toEqual({ codeOrQr: 'ABCD-2345' });
    lookup.flush(details);
    await Promise.resolve();

    backend.expectOne('/api/admin/operator/kiosks').flush(kiosks);
    await looking;
    fixture.detectChanges();
  }

  beforeEach(() => TestBed.resetTestingModule());

  afterEach(() => backend.verify());

  it('asks for a whole code before it looks anything up', () => {
    setUp(Permissions.kiosksActivate);

    for (const [code, valid] of [
      ['', false],
      ['ABCD', false],
      ['ABCD-234', false],
      ['ABCD-2345', true],
      ['abcd2345', true],
      ['ABCD-23456', false],
    ] as const) {
      page.code.set(code);
      expect(page.codeValid(), code).toBe(valid);
    }

    void page.find();
    backend.expectNone('/api/admin/devices/lookup');
  });

  it('offers no camera where the browser cannot read a QR', () => {
    setUp(Permissions.kiosksActivate);

    // The test browser has no BarcodeDetector: only the code is asked for.
    expect(page.canScan).toBe(false);
    expect(text()).not.toContain('operator.activate.scan');
    expect(text()).toContain('operator.activate.find');
  });

  it('says that nothing was found, and what to do, when the code is wrong', async () => {
    setUp(Permissions.kiosksActivate);
    page.code.set('ZZZZ-9999');

    const looking = page.find();

    backend.expectOne('/api/admin/devices/lookup').flush({ code: 'notFound' }, { status: 404, statusText: 'Not Found' });
    await looking;
    fixture.detectChanges();

    expect(page.device()).toBeNull();
    expect(text()).toContain('operator.activate.notFound');
    expect(text(), 'the code is still there to correct').toContain('operator.activate.find');
  });

  it('shows the machine, and the one kiosk that waits for one already chosen', async () => {
    setUp(Permissions.kiosksActivate);

    await found([
      kiosk(),
      kiosk({ id: 'k-3', code: 'KSK-0003', name: 'Station', status: 'NotActivated' }),
      kiosk({ id: 'k-2', code: 'KSK-0002', name: 'Mall', status: 'Offline' }),
    ]);

    expect(text()).toContain('kiosk-new');
    expect(page.kiosks()?.map((item) => item.code), 'the one waiting for a machine first').toEqual([
      'KSK-0003',
      'KSK-0001',
      'KSK-0002',
    ]);
    expect(page.kioskId()).toBe('k-3');
    expect(page.printer(), 'its only printer').toBe('HP LaserJet');
    expect(page.replaces()).toBe(false);
    expect(text()).not.toContain('operator.activate.replaces');
  });

  it('chooses nothing by itself when the choice is not obvious, and warns before a machine is replaced', async () => {
    setUp(Permissions.kiosksActivate);

    await found([kiosk(), kiosk({ id: 'k-2', code: 'KSK-0002', status: 'Offline' })]);

    expect(page.kioskId(), 'no kiosk waits for a machine').toBeNull();

    page.kioskId.set('k-1');
    fixture.detectChanges();

    expect(page.replaces()).toBe(true);
    expect(text()).toContain('operator.activate.replaces');
  });

  it('says so when the account has no kiosk to bind the machine to', async () => {
    setUp(Permissions.kiosksActivate);

    await found([]);

    expect(text()).toContain('operator.activate.noKiosks');
    expect(text()).not.toContain('operator.activate.confirm');
  });

  it('activates onto the chosen kiosk once, and opens that kiosk', async () => {
    setUp(Permissions.kiosksActivate);

    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    await found([kiosk({ id: 'k-3', code: 'KSK-0003', status: 'NotActivated' })]);

    const activating = page.activate();

    // A second press while the first is on its way sends nothing.
    void page.activate();

    const request = backend.expectOne('/api/admin/devices/d-1/activate');
    expect(request.request.body).toEqual({ kioskId: 'k-3', selectedPrinterName: 'HP LaserJet' });
    request.flush({ deviceId: 'd-1', kioskId: 'k-3', kioskCode: 'KSK-0003', kioskStatus: 'NotConfigured' });
    await activating;

    expect(notify.success).toHaveBeenCalledWith('operator.activate.done');
    expect(navigate).toHaveBeenCalledWith(['/operator/kiosks', 'k-3']);
  });

  it('stays on the page and says why when the server refuses', async () => {
    setUp(Permissions.kiosksActivate);

    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    await found([kiosk({ id: 'k-3', status: 'NotActivated' })]);

    const activating = page.activate();

    backend.expectOne('/api/admin/devices/d-1/activate').flush(null, { status: 403, statusText: 'Forbidden' });
    await activating;

    expect(notify.error).toHaveBeenCalledTimes(1);
    expect(navigate).not.toHaveBeenCalled();
    expect(page.busy(), 'the button is back').toBe(false);
  });

  it('points to the "Cihazlar" page for a new kiosk only for those who may make one', () => {
    setUp(Permissions.kiosksActivate);
    expect(text()).not.toContain('operator.activate.newKiosk');

    TestBed.resetTestingModule();
    setUp(Permissions.kiosksActivate, Permissions.kiosksCreate, Permissions.devicesView);
    expect(text()).toContain('operator.activate.newKiosk');
  });
});

describe('the QR a kiosk shows', () => {
  it('is told from any other code the camera may see', () => {
    expect(activationQr('neoprint://activate/2b1d0c1e-0000-4000-8000-000000000001?code=ABCD-2345')).toBe(true);
    expect(activationQr('NEOPRINT://ACTIVATE/x')).toBe(true);
    expect(activationQr('neoprint://pay/123')).toBe(false);
    expect(activationQr('https://example.test/activate')).toBe(false);
  });
});
