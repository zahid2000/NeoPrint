import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { describe, beforeEach, afterEach, expect, it, vi } from 'vitest';

import {
  DeviceListItem,
  EnrollmentTokenListItem,
} from '../../core/api/admin-api.service';
import { DevicesComponent } from './devices.component';

function device(overrides: Partial<DeviceListItem> = {}): DeviceListItem {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    status: 'Pending',
    platform: 'Windows',
    appVersion: '0.1.0',
    agentVersion: '0.1.0',
    hostname: 'KIOSK-01',
    os: 'Windows 11 Pro',
    lastIp: '10.0.0.5',
    registeredAt: new Date().toISOString(),
    lastSeenAt: new Date().toISOString(),
    kioskId: null,
    kioskCode: null,
    hasValidPairingCode: true,
    pairingCodeExpiresAt: new Date(Date.now() + 600_000).toISOString(),
    reportedPrinters: ['HP LaserJet'],
    ...overrides,
  };
}

function token(
  overrides: Partial<EnrollmentTokenListItem> = {},
): EnrollmentTokenListItem {
  return {
    id: '99999999-9999-9999-9999-999999999999',
    kioskId: null,
    kioskCode: null,
    newKioskName: null,
    createdAt: new Date().toISOString(),
    expiresAt: new Date(Date.now() + 86_400_000).toISOString(),
    usedByDeviceId: null,
    revokedAt: null,
    isUsable: true,
    ...overrides,
  };
}

async function flushMicrotasks(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

describe('DevicesComponent', () => {
  let fixture: ComponentFixture<DevicesComponent>;
  let component: DevicesComponent;
  let backend: HttpTestingController;

  /** Answers the four requests the page makes when it loads. */
  async function load(options: {
    pending?: DeviceListItem[];
    active?: DeviceListItem[];
    revoked?: DeviceListItem[];
    tokens?: EnrollmentTokenListItem[];
  }): Promise<void> {
    backend
      .match((request) => request.url === '/api/admin/devices')
      .forEach((request) => {
        const status = request.request.params.get('status');

        request.flush(
          status === 'Pending'
            ? (options.pending ?? [])
            : status === 'Active'
              ? (options.active ?? [])
              : (options.revoked ?? []),
        );
      });

    await flushMicrotasks();

    backend
      .expectOne('/api/admin/enrollment-tokens')
      .flush(options.tokens ?? []);

    await fixture.whenStable();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'login', children: [] }]),
        provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(DevicesComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    backend.verify();
  });

  it('sorts the devices into the tab each one belongs in', async () => {
    await load({
      pending: [device()],
      active: [
        device({
          id: '22222222-2222-2222-2222-222222222222',
          status: 'Active',
          kioskId: '33333333-3333-3333-3333-333333333333',
          kioskCode: 'KSK-0001',
        }),
      ],
      revoked: [device({ id: '44444444-4444-4444-4444-444444444444', status: 'Revoked' })],
    });

    expect(component.pending()).toHaveLength(1);
    expect(component.active()).toHaveLength(1);
    expect(component.revoked()).toHaveLength(1);
    expect(component.errorMessage()).toBeNull();
  });

  it('shows an empty token list rather than an error when the permission is missing', async () => {
    backend
      .match((request) => request.url === '/api/admin/devices')
      .forEach((request) => request.flush([]));

    await flushMicrotasks();

    // devices.enroll is a separate permission from devices.view.
    backend
      .expectOne('/api/admin/enrollment-tokens')
      .flush('', { status: 403, statusText: 'Forbidden' });

    await fixture.whenStable();

    expect(component.tokens()).toEqual([]);
    expect(component.errorMessage()).toBeNull();
  });

  it('describes each token by what can still be done with it', async () => {
    await load({});

    expect(component.tokenStatusKey(token())).toBe('tokens.usable');
    expect(
      component.tokenStatusKey(
        token({ usedByDeviceId: '55555555-5555-5555-5555-555555555555' }),
      ),
    ).toBe('tokens.used');
    expect(
      component.tokenStatusKey(
        token({ revokedAt: new Date().toISOString(), isUsable: false }),
      ),
    ).toBe('tokens.revoked');
    expect(component.tokenStatusKey(token({ isUsable: false }))).toBe(
      'tokens.expired',
    );
  });

  it('looks a device up by the code shown on the kiosk', async () => {
    await load({});

    component.lookupForm.setValue({ code: 'K7QM-4XPT' });

    const looked = component.lookup();

    const request = backend.expectOne('/api/admin/devices/lookup');

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ codeOrQr: 'K7QM-4XPT' });

    request.flush('', { status: 404, statusText: 'Not Found' });

    await looked;

    expect(component.lookupBusy()).toBe(false);
  });

  it('asks before detaching a device and passes the reason on', async () => {
    await load({ active: [device({ status: 'Active' })] });

    vi.spyOn(window, 'prompt').mockReturnValue('Machine replaced');

    const revoked = component.revoke(component.active()[0]);

    const request = backend.expectOne(
      '/api/admin/devices/11111111-1111-1111-1111-111111111111/revoke',
    );

    // The reason lands in the audit log (plan section 8A.1).
    expect(request.request.body).toEqual({ reason: 'Machine replaced' });

    request.flush(null, { status: 204, statusText: 'No Content' });

    await flushMicrotasks();

    await load({});
    await revoked;
  });

  it('detaches nothing when the question is dismissed', async () => {
    await load({ active: [device({ status: 'Active' })] });

    vi.spyOn(window, 'prompt').mockReturnValue(null);

    await component.revoke(component.active()[0]);

    backend.expectNone(
      '/api/admin/devices/11111111-1111-1111-1111-111111111111/revoke',
    );
  });

  it('reports a failed load without leaving stale rows on screen', async () => {
    backend
      .match((request) => request.url === '/api/admin/devices')
      .forEach((request) =>
        request.flush(
          { detail: 'Serverdə xəta baş verdi.' },
          { status: 500, statusText: 'Server Error' },
        ),
      );

    await fixture.whenStable();

    expect(component.errorMessage()).toBe('Serverdə xəta baş verdi.');
    expect(component.pending()).toEqual([]);
    expect(component.loading()).toBe(false);
  });
});
