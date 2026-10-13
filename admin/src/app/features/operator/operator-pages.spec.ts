import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { FakeHub } from '../../core/realtime/fake-hub.testing';
import { liveDebounceMs } from '../../core/realtime/live-refresh';
import { concernsOf } from './operator-kiosk-card.component';
import { OperatorKiosksComponent } from './operator-kiosks.component';
import { OperatorTasksComponent } from './operator-tasks.component';
import { OperatorKioskCard, ServiceTask } from './operator.models';

const me = 'a-1';

function card(overrides: Partial<OperatorKioskCard> = {}): OperatorKioskCard {
  return {
    id: 'k-1',
    code: 'KSK-0001',
    name: 'Library',
    address: 'Main street 1',
    status: 'Online',
    lastHeartbeatAt: '2026-10-13T08:00:00Z',
    printer: { name: 'Printer', online: true, reasons: [], readAt: '2026-10-13T08:00:00Z', stale: false },
    paper: { A4: { capacity: 500, current: 400, counted: true } },
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

function task(overrides: Partial<ServiceTask> = {}): ServiceTask {
  return {
    id: 't-1',
    kioskId: 'k-1',
    kioskCode: 'KSK-0001',
    kioskName: 'Library',
    type: 'Cleaning',
    priority: 'Normal',
    status: 'Open',
    source: 'Manual',
    createdAt: '2026-10-13T07:00:00Z',
    ...overrides,
  };
}

function account(...permissions: string[]) {
  const admin = { id: me, email: 'operator@example.test', fullName: 'Operator', roles: [], permissions, allKiosks: false, kioskIds: ['k-1'] };

  return {
    admin: () => admin,
    can: (permission: string) => permissions.includes(permission),
    canAny: (list: readonly string[]) => list.some((permission) => permissions.includes(permission)),
  };
}

function setUp(...permissions: string[]): { backend: HttpTestingController; hub: FakeHub } {
  const hub = new FakeHub();

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      provideTranslateService(),
      { provide: AdminHubService, useValue: hub },
      { provide: AuthService, useValue: account(...permissions) },
    ],
  });

  return { backend: TestBed.inject(HttpTestingController), hub };
}

describe('a kiosk card says why the kiosk needs somebody', () => {
  it('says nothing about a kiosk that is fine', () => {
    expect(concernsOf(card(), 'Online')).toEqual([]);
  });

  it('puts a Critical alert in the danger tone and a Warning in the warning tone, each with its own icon', () => {
    const concerns = concernsOf(
      card({
        alerts: [
          { id: 'a1', kioskId: 'k-1', resource: 'PaperA4', unit: 'Sheets', level: 'Critical', value: 20, raisedAt: '' },
          { id: 'a2', kioskId: 'k-1', resource: 'Toner', unit: 'Percent', level: 'Warning', value: 12, raisedAt: '' },
        ],
      }),
      'Online',
    );

    expect(concerns.map((concern) => [concern.tone, concern.icon])).toEqual([
      ['danger', 'error'],
      ['warning', 'warning'],
    ]);
    expect(concerns[0].translate).toEqual({ resource: 'operator.resource.PaperA4', unit: 'operator.unit.Sheets' });
  });

  it('asks for a counter nobody has set rather than calling the tray empty (T-258)', () => {
    const concerns = concernsOf(card({ paper: { A4: { capacity: 500, current: 0, counted: false } } }), 'Online');

    expect(concerns).toHaveLength(1);
    expect(concerns[0].key).toBe('operator.card.paperUnset');
    expect(concerns[0].tone).toBe('warning');
  });

  it('says what the printer says, unless the reading is old or the kiosk is out of touch', () => {
    const stuck = card({ printer: { name: 'P', online: true, reasons: ['media-jam'], readAt: '', stale: false } });

    expect(concernsOf(stuck, 'Online')[0].key).toBe('operator.card.printerReasons');
    expect(concernsOf(stuck, 'Offline'), 'an offline kiosk says nothing true about its printer').toEqual([]);
    expect(concernsOf(card({ printer: { name: 'P', online: false, reasons: [], readAt: '', stale: true } }), 'Online')).toEqual([]);
    expect(
      concernsOf(card({ printer: { name: 'P', online: false, reasons: [], readAt: '', stale: false } }), 'Online')[0].key,
    ).toBe('operator.card.printerOffline');
  });
});

describe('OperatorKiosksComponent', () => {
  let fixture: ComponentFixture<OperatorKiosksComponent>;
  let backend: HttpTestingController;
  let hub: FakeHub;

  const element = (): HTMLElement => fixture.nativeElement as HTMLElement;

  async function settle(): Promise<void> {
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
  }

  beforeEach(async () => {
    vi.useFakeTimers();
    localStorage.clear();
    ({ backend, hub } = setUp(Permissions.operatorPanel, Permissions.kiosksView, Permissions.paperRefill));

    fixture = TestBed.createComponent(OperatorKiosksComponent);
    backend.expectOne('/api/admin/operator/kiosks').flush([card({ id: 'k-2', code: 'KSK-0002', attention: 80 }), card()]);
    await settle();
  });

  afterEach(() => {
    fixture.destroy();
    backend.verify();
    vi.useRealTimers();
    localStorage.clear();
  });

  it('lists a card a kiosk in the order the server gave, each with its refill button', () => {
    const cards = [...element().querySelectorAll('article')];

    expect(cards.map((item) => item.getAttribute('data-kiosk'))).toEqual(['KSK-0002', 'KSK-0001']);
    expect(cards[0].querySelector('button')?.textContent).toContain('operator.kiosks.refill');
    expect(element().textContent).toContain('operator.updated');
  });

  it('changes a card at once when its kiosk goes offline, without asking again', () => {
    hub.emit('KioskStatusChanged', { kioskId: 'k-1', status: 'Offline' });
    fixture.detectChanges();

    const second = element().querySelectorAll('article')[1];

    expect(second.querySelector('app-status-chip')?.textContent).toContain('kiosks.status.Offline');

    vi.advanceTimersByTime(liveDebounceMs);
    backend.expectOne('/api/admin/operator/kiosks').flush([card({ status: 'Offline' })]);
  });

  it('says the data is old and switches the buttons off when the server cannot be reached', async () => {
    hub.emit('KioskStatusChanged', { kioskId: 'k-1', status: 'Offline' });
    vi.advanceTimersByTime(liveDebounceMs);
    backend.expectOne('/api/admin/operator/kiosks').error(new ProgressEvent('error'));
    await settle();

    expect(element().querySelector('.neo-notice')?.textContent).toContain('operator.offline');
    expect(element().querySelectorAll('article'), 'the cards are still there to read').toHaveLength(2);
    expect([...element().querySelectorAll('article button')].every((button) => (button as HTMLButtonElement).disabled)).toBe(true);
  });
});

describe('OperatorTasksComponent', () => {
  let fixture: ComponentFixture<OperatorTasksComponent>;
  let backend: HttpTestingController;

  const element = (): HTMLElement => fixture.nativeElement as HTMLElement;
  const tasksRequest = (request: { url: string }): boolean => request.url === '/api/admin/service-tasks';
  const buttonOf = (id: string): HTMLButtonElement =>
    element().querySelector(`[data-task="${id}"] button.op-primary`) as HTMLButtonElement;

  async function settle(): Promise<void> {
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
  }

  const page = (items: ServiceTask[]) => ({ items, total: items.length, page: 1, pageSize: 100 });

  beforeEach(async () => {
    vi.useFakeTimers();
    localStorage.clear();
    ({ backend } = setUp(Permissions.operatorPanel, Permissions.serviceTasksView, Permissions.serviceTasksComplete));

    fixture = TestBed.createComponent(OperatorTasksComponent);

    const request = backend.expectOne(tasksRequest);
    expect(request.request.params.getAll('status')).toEqual(['Open', 'Assigned', 'InProgress']);
    request.flush(
      page([
        task({ id: 't-mine', status: 'Assigned', assignedTo: me, priority: 'Critical', type: 'RefillPaper' }),
        task({ id: 't-doing', status: 'InProgress', assignedTo: me }),
        task({ id: 't-pool', status: 'Open' }),
        task({ id: 't-other', status: 'Assigned', assignedTo: 'somebody-else' }),
      ]),
    );
    await settle();
  });

  afterEach(() => {
    fixture.destroy();
    backend.verify();
    vi.useRealTimers();
    localStorage.clear();
  });

  it('shows what is this operator\'s and what is in the pool, and nothing of anybody else\'s', () => {
    const shown = [...element().querySelectorAll('article')].map((item) => item.getAttribute('data-task'));

    expect(shown).toEqual(['t-mine', 't-doing', 't-pool']);
  });

  it('names the next step on each task\'s one button', () => {
    expect(buttonOf('t-pool').textContent).toContain('operator.tasks.accept');
    expect(buttonOf('t-mine').textContent).toContain('operator.tasks.start');
    expect(buttonOf('t-doing').textContent).toContain('operator.tasks.complete');
  });

  it('takes a task from the pool: it is this operator\'s at once, and the list is asked for behind it', async () => {
    buttonOf('t-pool').click();

    const accept = backend.expectOne('/api/admin/service-tasks/t-pool/accept');
    expect(accept.request.method).toBe('POST');
    accept.flush(task({ id: 't-pool', status: 'Assigned', assignedTo: me }));
    await settle();

    expect(buttonOf('t-pool').textContent, 'the next step now').toContain('operator.tasks.start');

    backend.expectOne(tasksRequest).flush(page([task({ id: 't-pool', status: 'Assigned', assignedTo: me })]));
  });

  it('starts a task that was given to this operator', async () => {
    buttonOf('t-mine').click();

    backend
      .expectOne('/api/admin/service-tasks/t-mine/start')
      .flush(task({ id: 't-mine', status: 'InProgress', assignedTo: me, type: 'RefillPaper' }));
    await settle();

    expect(buttonOf('t-mine').textContent).toContain('operator.tasks.complete');

    backend.expectOne(tasksRequest).flush(page([task({ id: 't-mine', status: 'InProgress', assignedTo: me })]));
  });

  it('asks again and keeps the list when a step is refused', async () => {
    buttonOf('t-pool').click();

    backend
      .expectOne('/api/admin/service-tasks/t-pool/accept')
      .flush({ title: 'Taken by somebody else' }, { status: 409, statusText: 'Conflict' });
    await settle();

    // Somebody took it a moment before: the list says so once it is fetched again.
    backend.expectOne(tasksRequest).flush(page([task({ id: 't-mine', status: 'Assigned', assignedTo: me })]));
    await settle();

    expect([...element().querySelectorAll('article')].map((item) => item.getAttribute('data-task'))).toEqual(['t-mine']);
  });
});
