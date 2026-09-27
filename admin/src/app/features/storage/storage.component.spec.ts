import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { StorageProvider, StorageSyncJob } from '../../core/api/step2.models';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { StorageComponent } from './storage.component';

/** Stands in for the SignalR connection: the test fires the events itself. */
class FakeHub {
  readonly handlers = new Map<string, (payload: unknown) => void>();

  on<T>(event: string, handler: (payload: T) => void): () => void {
    this.handlers.set(event, handler as (payload: unknown) => void);
    return () => this.handlers.delete(event);
  }

  fire(event: string, payload: unknown): void {
    this.handlers.get(event)?.(payload);
  }

  connectionState(): string {
    return 'connected';
  }

  lastEventAt(): Date | null {
    return null;
  }
}

const primary: StorageProvider = {
  id: 'a',
  name: 'Local RustFS',
  type: 'RustFS',
  endpoint: 'http://rustfs:9000',
  region: 'us-east-1',
  useSsl: false,
  accessKeyHint: '••••stfs',
  buckets: { files: 'neoprint-files', receipts: 'neoprint-receipts', temp: 'neoprint-temp' },
  role: 'Primary',
  healthStatus: 'Healthy',
  isVerified: true,
};

const running: StorageSyncJob = {
  id: 'job',
  mode: 'Migration',
  sourceId: 'a',
  sourceName: 'Local RustFS',
  targetId: 'b',
  targetName: 'Second',
  status: 'Running',
  totalObjects: 100,
  copiedObjects: 10,
  failedObjects: 0,
  totalBytes: 1000,
  copiedBytes: 100,
  switchPrimaryOnComplete: true,
  sourcePurgedObjects: 0,
  sourceKeptObjects: 0,
};

describe('StorageComponent', () => {
  let fixture: ComponentFixture<StorageComponent>;
  let component: StorageComponent;
  let backend: HttpTestingController;
  let hub: FakeHub;

  beforeEach(async () => {
    hub = new FakeHub();

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
        { provide: AdminHubService, useValue: hub },
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(StorageComponent);
    component = fixture.componentInstance;

    backend.expectOne('/api/admin/storage/providers').flush([primary]);
    backend.expectOne('/api/admin/storage/sync-jobs').flush([running]);
    await fixture.whenStable();
  });

  afterEach(() => backend.verify());

  it('shows the running job as the active one', () => {
    expect(component.activeJob()?.id).toBe('job');
  });

  it('moves the progress on and works out a speed from the hub', () => {
    hub.fire('StorageSyncProgress', { ...running, jobId: 'job', copiedObjects: 20, copiedBytes: 200, at: '2026-09-27T10:00:00Z' });
    hub.fire('StorageSyncProgress', { ...running, jobId: 'job', copiedObjects: 30, copiedBytes: 400, at: '2026-09-27T10:00:02Z' });

    expect(component.activeJob()?.copiedObjects).toBe(30);

    // 200 bytes in 2 seconds.
    expect(component.speed()).toBe(100);
  });

  it('marks a provider unhealthy the moment the hub says so', () => {
    hub.fire('StorageHealthChanged', {
      providerId: 'a',
      name: 'Local RustFS',
      role: 'Primary',
      status: 'Unhealthy',
      message: 'Provayderə qoşulmaq mümkün olmadı.',
      at: '2026-09-27T10:01:00Z',
    });

    expect(component.providers()[0].healthStatus).toBe('Unhealthy');
    expect(component.providers()[0].healthMessage).toContain('qoşulmaq');
  });

  it('offers to empty the source only after the retention period', () => {
    const completed: StorageSyncJob = { ...running, status: 'Completed', finishedAt: '2026-09-01T00:00:00Z' };

    expect(component.purgeState({ ...completed, sourcePurgeAllowedFrom: '2999-01-01T00:00:00Z' })).toBe('waiting');
    expect(component.purgeState({ ...completed, sourcePurgeAllowedFrom: '2026-09-02T00:00:00Z' })).toBe('ready');
    expect(component.purgeState({ ...completed, sourcePurgeRequestedAt: '2026-10-02T00:00:00Z' })).toBe('running');
    expect(component.purgeState({ ...completed, sourcePurgedAt: '2026-10-02T00:00:00Z' })).toBe('done');
    expect(component.purgeState({ ...completed, mode: 'Mirror' })).toBe('none');
  });
});
