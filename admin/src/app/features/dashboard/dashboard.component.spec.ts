import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { KioskListItem } from '../../core/api/admin-api.service';
import { IncidentRow, Page } from '../../core/api/step4.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { FakeHub } from '../../core/realtime/fake-hub.testing';
import { liveDebounceMs, livePollMs } from '../../core/realtime/live-refresh';
import { DashboardComponent } from './dashboard.component';

function kiosk(overrides: Partial<KioskListItem> = {}): KioskListItem {
  return {
    id: 'k-1',
    code: 'KSK-0001',
    name: 'Library',
    address: 'Main street 1',
    status: 'Online',
    activeDeviceId: 'd-1',
    lastHeartbeatAt: null,
    ...overrides,
  } as KioskListItem;
}

function incident(overrides: Partial<IncidentRow> = {}): IncidentRow {
  return {
    id: 'i-1',
    type: 'KioskOffline',
    severity: 'High',
    status: 'Open',
    details: 'The kiosk has not been heard from.',
    kioskId: 'k-1',
    kioskCode: 'KSK-0001',
    createdAt: '2026-10-13T08:00:00Z',
    ...overrides,
  };
}

const page = (items: IncidentRow[], total = items.length): Page<IncidentRow> => ({
  items,
  total,
  page: 1,
  pageSize: 5,
});

/** An account that sees the kiosks and the incidents and nothing else, so only those are asked for. */
function account(...permissions: string[]) {
  const admin = {
    id: 'a-1',
    email: 'admin@example.test',
    fullName: 'Admin',
    roles: [],
    permissions,
    allKiosks: true,
    kioskIds: [],
    mustChangePassword: false,
  };

  return {
    admin: () => admin,
    can: (permission: string) => permissions.includes(permission),
    canAny: (list: readonly string[]) => list.some((permission) => permissions.includes(permission)),
  };
}

// The words of a status chip, without its icon.
const statusChips = '.dashboard__statuses .chip > span';
const incidentChips = '[data-tile="incidents"] .chip > span';

const incidentsUrl = (request: { url: string }): boolean => request.url === '/api/admin/incidents';

/**
 * The dashboard follows the kiosks and the incidents without a reload, on
 * screen (T-253, T-255): what the reader sees is checked, not the signals.
 */
describe('DashboardComponent', () => {
  let fixture: ComponentFixture<DashboardComponent>;
  let backend: HttpTestingController;
  let hub: FakeHub;

  const element = (): HTMLElement => fixture.nativeElement as HTMLElement;

  const text = (selector: string): string[] => {
    fixture.detectChanges();

    return [...element().querySelectorAll(selector)].map(
      (node) => node.textContent?.replace(/\s+/g, ' ').trim() ?? '',
    );
  };

  async function settle(): Promise<void> {
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
  }

  async function answerKiosks(list: KioskListItem[]): Promise<void> {
    backend.expectOne('/api/admin/kiosks').flush(list);
    await settle();
  }

  async function answerIncidents(body: Page<IncidentRow>): Promise<void> {
    const request = backend.expectOne(incidentsUrl);

    expect(request.request.params.getAll('status')).toEqual(['Open', 'Acknowledged']);
    request.flush(body);
    await settle();
  }

  beforeEach(async () => {
    vi.useFakeTimers();
    hub = new FakeHub();

    TestBed.configureTestingModule({
      imports: [DashboardComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        provideTranslateService(),
        { provide: AdminHubService, useValue: hub },
        {
          provide: AuthService,
          useValue: account(Permissions.kiosksView, Permissions.incidentsView),
        },
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(DashboardComponent);

    await answerKiosks([kiosk(), kiosk({ id: 'k-2', code: 'KSK-0002', name: 'Station' })]);
    await answerIncidents(page([]));
  });

  afterEach(() => {
    fixture.destroy();
    backend.verify();
    vi.useRealTimers();
  });

  it('moves a kiosk from Online to Offline in the counts and lists it as needing attention', async () => {
    expect(text(statusChips)).toEqual(['kiosks.status.Online: 2']);
    expect(element().textContent).toContain('dashboard.kiosksAllFine');

    hub.emit('KioskStatusChanged', { kioskId: 'k-1', status: 'Offline' });

    expect(text(statusChips)).toEqual([
      'kiosks.status.Online: 1',
      'kiosks.status.Offline: 1',
    ]);

    const attention = text('.dashboard__list li');

    expect(attention).toHaveLength(1);
    expect(attention[0]).toContain('KSK-0001');
    expect(attention[0]).toContain('dashboard.attention.offline');

    // The fetch that brings the rest of the row follows the event.
    vi.advanceTimersByTime(liveDebounceMs);
    await answerKiosks([kiosk({ status: 'Offline' }), kiosk({ id: 'k-2', code: 'KSK-0002' })]);
  });

  it('takes the kiosk off the attention list when it comes back', async () => {
    hub.emit('KioskStatusChanged', { kioskId: 'k-1', status: 'Offline' });
    hub.emit('KioskStatusChanged', { kioskId: 'k-1', status: 'Online' });

    expect(text(statusChips)).toEqual(['kiosks.status.Online: 2']);
    expect(text('.dashboard__list li')).toHaveLength(0);

    vi.advanceTimersByTime(liveDebounceMs);
    await answerKiosks([kiosk(), kiosk({ id: 'k-2', code: 'KSK-0002' })]);
  });

  it('says there is no open incident, then counts and lists one that is raised', async () => {
    expect(text(incidentChips)).toEqual(['dashboard.incidentsNone']);

    hub.emit('IncidentRaised', { incidentId: 'i-1', type: 'KioskOffline', severity: 'High', kioskId: 'k-1' });

    // Nothing is guessed from the event: the server counts, the tile asks.
    backend.expectNone(incidentsUrl);
    vi.advanceTimersByTime(liveDebounceMs);
    await answerIncidents(page([incident()]));

    expect(text('[data-metric="open-incidents"]')).toEqual(['1']);

    const rows = text('[data-tile="incidents"] li');

    expect(rows).toHaveLength(1);
    expect(rows[0]).toContain('money.incidentSeverity.High');
    expect(rows[0]).toContain('KSK-0001');
    expect(rows[0]).toContain('The kiosk has not been heard from.');
  });

  it('counts down when an incident is resolved, and says how many more there are than it lists', async () => {
    hub.emit('IncidentRaised', { incidentId: 'i-9' });
    vi.advanceTimersByTime(liveDebounceMs);
    await answerIncidents(
      page(
        [1, 2, 3, 4, 5].map((n) => incident({ id: `i-${n}`, severity: n === 1 ? 'Critical' : 'High' })),
        7,
      ),
    );

    expect(text('[data-metric="open-incidents"]')).toEqual(['7']);
    expect(text('[data-tile="incidents"] li')).toHaveLength(5);
    expect(element().querySelector('[data-tile="incidents"]')?.textContent).toContain(
      'dashboard.incidentsMore',
    );

    hub.emit('IncidentResolved', { incidentId: 'i-1', status: 'Resolved' });
    vi.advanceTimersByTime(liveDebounceMs);
    await answerIncidents(page([]));

    expect(text(incidentChips)).toEqual(['dashboard.incidentsNone']);
    expect(element().querySelector('[data-metric="open-incidents"]')).toBeNull();
  });

  it('asks for both again when the connection comes back, and polls while it is away', async () => {
    hub.drop();
    vi.advanceTimersByTime(livePollMs);

    await answerKiosks([kiosk({ status: 'Paused' }), kiosk({ id: 'k-2', code: 'KSK-0002' })]);
    await answerIncidents(page([incident()]));

    expect(text(statusChips)).toContain('kiosks.status.Paused: 1');
    expect(text('[data-metric="open-incidents"]')).toEqual(['1']);

    hub.comeBack();

    await answerKiosks([kiosk(), kiosk({ id: 'k-2', code: 'KSK-0002' })]);
    await answerIncidents(page([]));

    expect(text(statusChips)).toEqual(['kiosks.status.Online: 2']);
    expect(text(incidentChips)).toEqual(['dashboard.incidentsNone']);
  });
});
