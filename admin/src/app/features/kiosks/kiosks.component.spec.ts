import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { KioskListItem } from '../../core/api/admin-api.service';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { FakeHub } from '../../core/realtime/fake-hub.testing';
import { liveDebounceMs, livePollMs } from '../../core/realtime/live-refresh';
import { KiosksComponent } from './kiosks.component';

function kiosk(overrides: Partial<KioskListItem> = {}): KioskListItem {
  return {
    id: 'k-1',
    code: 'KSK-0001',
    name: 'Library',
    address: 'Main street 1',
    status: 'Online',
    ...overrides,
  } as KioskListItem;
}

/**
 * The kiosk list follows its kiosks without a reload (T-248): a panel left
 * open showed a kiosk as Online long after it had gone offline, and as Offline
 * after it had come back, until somebody pressed F5.
 */
describe('KiosksComponent', () => {
  let fixture: ComponentFixture<KiosksComponent>;
  let component: KiosksComponent;
  let backend: HttpTestingController;
  let hub: FakeHub;

  /** Answers the list request that is waiting, and lets the page take it in. */
  async function answer(list: KioskListItem[]): Promise<void> {
    backend.expectOne('/api/admin/kiosks').flush(list);
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
  }

  const statusOf = (id: string): string | undefined =>
    component.kiosks().find((item) => item.id === id)?.status;

  /** The status chip as it is on screen, in the row of the kiosk with this code. */
  function shownStatus(code: string): string | undefined {
    fixture.detectChanges();

    const rows = [...(fixture.nativeElement as HTMLElement).querySelectorAll('tr[mat-row]')];
    const row = rows.find((candidate) => candidate.textContent?.includes(code));

    return row?.querySelector('app-status-chip')?.textContent?.replace(/\s+/g, ' ').trim();
  }

  beforeEach(async () => {
    vi.useFakeTimers();
    hub = new FakeHub();

    TestBed.configureTestingModule({
      imports: [KiosksComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        provideTranslateService(),
        { provide: AdminHubService, useValue: hub },
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(KiosksComponent);
    component = fixture.componentInstance;

    await answer([kiosk(), kiosk({ id: 'k-2', code: 'KSK-0002', status: 'Online' })]);
  });

  afterEach(() => {
    fixture.destroy();
    backend.verify();
    vi.useRealTimers();
  });

  it('shows a kiosk that went offline at once, from what the event says', () => {
    hub.emit('KioskStatusChanged', { kioskId: 'k-1', status: 'Offline' });

    expect(statusOf('k-1')).toBe('Offline');
    expect(statusOf('k-2'), 'the other kiosk is untouched').toBe('Online');

    // The fetch that brings the rest of the row has not gone yet.
    backend.expectNone('/api/admin/kiosks');
    vi.advanceTimersByTime(liveDebounceMs);
    backend.expectOne('/api/admin/kiosks').flush([kiosk({ status: 'Offline' }), kiosk({ id: 'k-2' })]);
  });

  it('shows the kiosk back online without a reload, and then fetches the rest of its row', async () => {
    hub.emit('KioskStatusChanged', { kioskId: 'k-1', status: 'Offline' });
    hub.emit('KioskStatusChanged', { kioskId: 'k-1', status: 'Online' });

    expect(statusOf('k-1')).toBe('Online');

    vi.advanceTimersByTime(liveDebounceMs);
    await answer([kiosk({ lastHeartbeatAt: '2026-10-12T10:00:00Z' }), kiosk({ id: 'k-2' })]);

    expect(component.kiosks()[0].lastHeartbeatAt).toBe('2026-10-12T10:00:00Z');
    expect(component.loading(), 'a quiet fetch never blanks the table').toBe(false);
  });

  it('fetches the list again when the connection comes back', async () => {
    hub.drop();
    hub.comeBack();

    // Whatever happened while the panel was cut off was not heard.
    await answer([kiosk({ status: 'Paused' }), kiosk({ id: 'k-2' })]);

    expect(statusOf('k-1')).toBe('Paused');
  });

  it('asks every thirty seconds while there is no live connection', async () => {
    hub.drop();

    vi.advanceTimersByTime(livePollMs);
    await answer([kiosk({ status: 'Offline' }), kiosk({ id: 'k-2' })]);
    expect(statusOf('k-1')).toBe('Offline');

    vi.advanceTimersByTime(livePollMs);
    await answer([kiosk({ status: 'Online' }), kiosk({ id: 'k-2' })]);
    expect(statusOf('k-1')).toBe('Online');
  });

  it('keeps what is on screen, and shows no error, when a quiet fetch fails', async () => {
    hub.drop();
    vi.advanceTimersByTime(livePollMs);

    backend.expectOne('/api/admin/kiosks').error(new ProgressEvent('error'));
    await vi.advanceTimersByTimeAsync(0);

    expect(component.kiosks()).toHaveLength(2);
    expect(component.problem(), 'the indicator in the header already says the panel is cut off').toBeNull();
  });

  // What the reader sees, not only what the component holds (T-255): the first
  // fix for T-248 was checked on the signal alone.
  it('changes the status chip in the row when the kiosk goes offline and comes back', () => {
    expect(shownStatus('KSK-0001')).toContain('kiosks.status.Online');

    hub.emit('KioskStatusChanged', { kioskId: 'k-1', status: 'Offline' });

    expect(shownStatus('KSK-0001')).toContain('kiosks.status.Offline');
    expect(shownStatus('KSK-0001')).not.toContain('kiosks.status.Online');
    expect(shownStatus('KSK-0002'), 'the other row is untouched').toContain('kiosks.status.Online');

    hub.emit('KioskStatusChanged', { kioskId: 'k-1', status: 'Online' });

    expect(shownStatus('KSK-0001')).toContain('kiosks.status.Online');

    vi.advanceTimersByTime(liveDebounceMs);
    backend.expectOne('/api/admin/kiosks').flush([kiosk(), kiosk({ id: 'k-2', code: 'KSK-0002' })]);
  });

  it('shows on screen what a poll found while the connection was down', async () => {
    hub.drop();
    vi.advanceTimersByTime(livePollMs);
    await answer([kiosk({ status: 'Paused' }), kiosk({ id: 'k-2', code: 'KSK-0002' })]);

    expect(shownStatus('KSK-0001')).toContain('kiosks.status.Paused');
  });

  it('fetches again when a device is activated or revoked', () => {
    hub.emit('DeviceRevoked', { deviceId: 'd-1' });
    vi.advanceTimersByTime(liveDebounceMs);

    backend.expectOne('/api/admin/kiosks').flush([kiosk({ status: 'NotActivated' }), kiosk({ id: 'k-2' })]);
  });
});
