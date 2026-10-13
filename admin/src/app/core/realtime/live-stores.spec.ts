import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import {
  EnvironmentInjector,
  createEnvironmentInjector,
  runInInjectionContext,
  signal,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { KioskListItem } from '../api/admin-api.service';
import { AuthService } from '../auth/auth.service';
import { AdminHubService } from './admin-hub.service';
import { FakeHub } from './fake-hub.testing';
import { IncidentLiveStore } from './incident-live.store';
import { KioskLiveStore } from './kiosk-live.store';
import { liveDebounceMs } from './live-refresh';

function kiosk(overrides: Partial<KioskListItem> = {}): KioskListItem {
  return { id: 'k-1', code: 'KSK-0001', name: 'Library', status: 'Online', ...overrides } as KioskListItem;
}

const incidentsUrl = (request: { url: string }): boolean => request.url === '/api/admin/incidents';

/**
 * The two stores every page reads the kiosks' statuses and the open incidents
 * from (T-254).
 */
describe('live stores', () => {
  let backend: HttpTestingController;
  let hub: FakeHub;
  let account: ReturnType<typeof signal<{ id: string } | null>>;
  let page: EnvironmentInjector | null = null;

  /** A page opens and watches, the way a component's constructor does. */
  function open(watch: () => void): void {
    page = createEnvironmentInjector([], TestBed.inject(EnvironmentInjector));
    runInInjectionContext(page, watch);
  }

  function close(): void {
    page?.destroy();
    page = null;
  }

  async function settle(): Promise<void> {
    await vi.advanceTimersByTimeAsync(0);
    TestBed.tick();
  }

  beforeEach(() => {
    vi.useFakeTimers();
    hub = new FakeHub();
    account = signal<{ id: string } | null>({ id: 'a-1' });

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AdminHubService, useValue: hub },
        { provide: AuthService, useValue: { admin: account } },
      ],
    });

    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    close();
    backend.verify();
    vi.useRealTimers();
  });

  describe('KioskLiveStore', () => {
    let store: KioskLiveStore;

    beforeEach(() => (store = TestBed.inject(KioskLiveStore)));

    it('knows nothing and asks for nothing until a page watches', () => {
      expect(store.kiosks()).toBeNull();
      backend.expectNone('/api/admin/kiosks');
    });

    it('applies a status event to the list and to what a single kiosk is asked about', async () => {
      open(() => store.watch());
      backend.expectOne('/api/admin/kiosks').flush([kiosk(), kiosk({ id: 'k-2' })]);
      await settle();

      hub.emit('KioskStatusChanged', { kioskId: 'k-1', status: 'Offline' });

      expect(store.kiosks()?.map((item) => item.status)).toEqual(['Offline', 'Online']);
      expect(store.statusOf('k-1')).toBe('Offline');
      expect(store.statusOf('k-2')).toBe('Online');

      vi.advanceTimersByTime(liveDebounceMs);
      backend.expectOne('/api/admin/kiosks').flush([kiosk({ status: 'Offline' }), kiosk({ id: 'k-2' })]);
    });

    it('remembers a status heard while nobody watched, and fetches nothing for it', () => {
      hub.emit('KioskStatusChanged', { kioskId: 'k-7', status: 'Paused' });
      vi.advanceTimersByTime(liveDebounceMs);

      expect(store.statusOf('k-7')).toBe('Paused');
      backend.expectNone('/api/admin/kiosks');
    });

    it('shows a second page what it has at once, and fetches behind it', async () => {
      open(() => store.watch());
      backend.expectOne('/api/admin/kiosks').flush([kiosk()]);
      await settle();
      close();

      open(() => store.watch());

      expect(store.kiosks(), 'no blank table while the fetch is on its way').toHaveLength(1);
      expect(store.loading()).toBe(false);

      backend.expectOne('/api/admin/kiosks').flush([kiosk({ status: 'Maintenance' })]);
      await settle();

      expect(store.statusOf('k-1')).toBe('Maintenance');
    });

    it('stops fetching when the last page has gone', () => {
      open(() => store.watch());
      backend.expectOne('/api/admin/kiosks').flush([kiosk()]);
      close();

      hub.emit('DeviceRevoked', { deviceId: 'd-1' });
      vi.advanceTimersByTime(liveDebounceMs);

      backend.expectNone('/api/admin/kiosks');
    });

    it('takes what a page learned another way', () => {
      store.learn('k-1', 'NotConfigured');

      expect(store.statusOf('k-1')).toBe('NotConfigured');
    });

    it('says why there is no list when the first fetch fails, and keeps the list when a later one does', async () => {
      open(() => store.watch());
      backend.expectOne('/api/admin/kiosks').error(new ProgressEvent('error'));
      await settle();

      expect(store.problem()).not.toBeNull();
      expect(store.kiosks()).toBeNull();

      void store.load();
      backend.expectOne('/api/admin/kiosks').flush([kiosk()]);
      await settle();

      expect(store.problem()).toBeNull();

      hub.emit('KioskStatusChanged', { kioskId: 'k-1', status: 'Offline' });
      vi.advanceTimersByTime(liveDebounceMs);
      backend.expectOne('/api/admin/kiosks').error(new ProgressEvent('error'));
      await settle();

      expect(store.kiosks()).toHaveLength(1);
      expect(store.problem(), 'a quiet fetch that fails says nothing').toBeNull();
    });

    it('forgets everything when another account signs in on the tab', async () => {
      open(() => store.watch());
      backend.expectOne('/api/admin/kiosks').flush([kiosk()]);
      await settle();
      close();

      account.set({ id: 'a-2' });
      await settle();

      expect(store.kiosks(), 'the last account saw kiosks this one may not').toBeNull();
      expect(store.statusOf('k-1')).toBeUndefined();
    });

    it('drops an answer that was meant for the account before', async () => {
      open(() => store.watch());
      const request = backend.expectOne('/api/admin/kiosks');

      account.set({ id: 'a-2' });
      await settle();

      // A page is still open, so the new account's list is asked for ...
      const second = backend.expectOne('/api/admin/kiosks');

      // ... and the old answer, arriving late, is not shown to it.
      request.flush([kiosk({ code: 'KSK-OF-THE-OTHER' })]);
      await settle();
      expect(store.kiosks()).toBeNull();

      second.flush([kiosk({ id: 'k-9', code: 'KSK-0009' })]);
      await settle();
      expect(store.kiosks()?.map((item) => item.code)).toEqual(['KSK-0009']);
    });
  });

  describe('IncidentLiveStore', () => {
    let store: IncidentLiveStore;

    beforeEach(() => (store = TestBed.inject(IncidentLiveStore)));

    const answer = (total: number): void =>
      backend.expectOne(incidentsUrl).flush({ items: [], total, page: 1, pageSize: 5 });

    it('asks for the open incidents when a page watches, and again after an event', async () => {
      expect(store.open()).toBeNull();

      open(() => store.watch());
      answer(2);
      await settle();
      expect(store.open()).toBe(2);

      hub.emit('IncidentResolved', { incidentId: 'i-1' });
      vi.advanceTimersByTime(liveDebounceMs);
      answer(1);
      await settle();
      expect(store.open()).toBe(1);
    });

    it('asks for nothing while nobody watches', () => {
      hub.emit('IncidentRaised', { incidentId: 'i-1' });
      vi.advanceTimersByTime(liveDebounceMs);

      backend.expectNone(incidentsUrl);
    });

    it('keeps the last count when a later fetch fails, and says so only when there is none', async () => {
      open(() => store.watch());
      backend.expectOne(incidentsUrl).error(new ProgressEvent('error'));
      await settle();
      expect(store.failed()).toBe(true);

      void store.load();
      answer(3);
      await settle();
      expect(store.failed()).toBe(false);

      hub.emit('IncidentRaised', { incidentId: 'i-2' });
      vi.advanceTimersByTime(liveDebounceMs);
      backend.expectOne(incidentsUrl).error(new ProgressEvent('error'));
      await settle();

      expect(store.open()).toBe(3);
      expect(store.failed()).toBe(false);
    });

    it('forgets the count when another account signs in', async () => {
      open(() => store.watch());
      answer(4);
      await settle();
      close();

      account.set({ id: 'a-2' });
      await settle();

      expect(store.open()).toBeNull();
    });
  });
});
