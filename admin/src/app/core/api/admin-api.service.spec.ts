import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { AdminApiService } from './admin-api.service';

/** The headers the backend relies on (docs/API.md). */
describe('AdminApiService', () => {
  let api: AdminApiService;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(AdminApiService);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it('sends a destructive action’s reason URL-encoded, for the audit log', async () => {
    const deleting = firstValueFrom(api.deleteKiosk('k1', '  Səhv yaradılıb  '));
    const request = backend.expectOne('/api/admin/kiosks/k1');

    expect(request.request.headers.get('X-Audit-Reason')).toBe(encodeURIComponent('Səhv yaradılıb'));

    request.flush(null);
    await deleting;
  });

  it('sends no reason header when there is no reason', async () => {
    const pausing = firstValueFrom(api.controlStorageSync('job', 'pause'));
    const request = backend.expectOne('/api/admin/storage/sync-jobs/job/pause');

    expect(request.request.headers.has('X-Audit-Reason')).toBe(false);

    request.flush({});
    await pausing;
  });

  it('gives each idempotent action its own key, and purge both headers', async () => {
    const purging = firstValueFrom(api.purgeStorageSource('job', 'Köhnə mənbə'));
    const request = backend.expectOne('/api/admin/storage/sync-jobs/job/purge-source');

    expect(request.request.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
    expect(decodeURIComponent(request.request.headers.get('X-Audit-Reason')!)).toBe('Köhnə mənbə');

    request.flush({});
    await purging;
  });

  it('uses the plan’s routes for the storage roles', async () => {
    const primary = firstValueFrom(api.setStorageRole('p', 'Primary'));
    backend.expectOne({ method: 'POST', url: '/api/admin/storage/providers/p/set-primary' }).flush({});
    await primary;

    const off = firstValueFrom(api.setStorageRole('p', 'Inactive', 'Lazım deyil'));
    const request = backend.expectOne({ method: 'PUT', url: '/api/admin/storage/providers/p/role' });
    expect(request.request.body).toEqual({ role: 'Inactive' });
    request.flush({});
    await off;
  });
});
