import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService, TranslateService } from '@ngx-translate/core';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { SystemJobs } from '../../core/api/step7.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { translations } from '../../core/i18n/translations';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { FakeHub } from '../../core/realtime/fake-hub.testing';
import { liveDebounceMs } from '../../core/realtime/live-refresh';
import { SystemJobsComponent } from './system-jobs.component';

let backend: HttpTestingController;
let hub: FakeHub;

function setUp(roles: string[] = ['SuperAdmin'], permissions: string[] = [Permissions.usersView, Permissions.paymentsView]): void {
  hub = new FakeHub();

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      { provide: AdminHubService, useValue: hub },
      {
        provide: AuthService,
        useValue: {
          admin: signal({ id: 'a-1', roles }),
          can: (permission: string) => permissions.includes(permission),
          canAny: (list: readonly string[]) => list.some((permission) => permissions.includes(permission)),
        },
      },
    ],
  });

  TestBed.inject(TranslateService).setTranslation('az', translations.az);
  TestBed.inject(TranslateService).use('az');
  backend = TestBed.inject(HttpTestingController);
}

const text = (fixture: ComponentFixture<unknown>, selector?: string): string => {
  const host = fixture.nativeElement as HTMLElement;
  const node = selector ? host.querySelector<HTMLElement>(selector) : host;
  return (node?.textContent ?? '').replace(/\s+/g, ' ');
};

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  await fixture.whenStable();
  fixture.detectChanges();
}

const run = (overrides: object = {}) => ({
  startedAt: '2026-10-17T10:00:00Z',
  finishedAt: '2026-10-17T10:00:02Z',
  dryRun: false,
  succeeded: true,
  counts: {},
  findings: [],
  ...overrides,
});

function jobs(overrides: Partial<SystemJobs> = {}): SystemJobs {
  return {
    runs: {
      'dead-letters': run({
        counts: { queues: 2, messages: 3 },
        queues: [
          { name: 'receipt-consumer_error', messages: 3, oldestAt: new Date(Date.now() - 3 * 3_600_000).toISOString() },
          { name: 'refund-consumer_error', messages: 0 },
        ],
      }),
      'file-cleanup': run({
        dryRun: true,
        counts: { examined: 40, deleted: 2, deletedBytes: 2048, protected: 3, recent: 1, failed: 0 },
        findings: [{ kind: 'wouldDelete', ref: 'files/aa/bb/print-v1.pdf', amount: 1024 }],
      }),
      'wallet-ledger': run({
        counts: { checked: 12, mismatches: 1 },
        findings: [{ kind: 'walletMismatch', ref: 'u-1', amount: 5, other: 3, label: 'NP-ABC123' }],
      }),
      'payment-consistency': run({
        counts: { payments: 9, paidNotPrinted: 1, refundWithoutLedger: 0, walletPaymentWithoutLedger: 0, paidWithoutFiscal: 0 },
        findings: [{ kind: 'paidNotPrinted', ref: '12345678-aaaa-bbbb-cccc-dddddddddddd', amount: 0.4 }],
      }),
    },
    analytics: { today: 4, week: 9, kiosks: [{ kioskId: 'k-1', kioskCode: 'KSK-9002', today: 4, week: 9, lastUnknownName: 'brand_new_thing' }] },
    ...overrides,
  };
}

async function open(answer: SystemJobs, roles?: string[], permissions?: string[]): Promise<ComponentFixture<SystemJobsComponent>> {
  setUp(roles, permissions);
  const fixture = TestBed.createComponent(SystemJobsComponent);
  fixture.detectChanges();
  backend.expectOne('/api/admin/system/jobs').flush(answer);
  await settle(fixture);
  return fixture;
}

afterEach(() => {
  backend.verify();
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('Arxa plan işləri', () => {
  it('shows each check with its state, what it found, and the kiosk events not kept', async () => {
    const fixture = await open(jobs());
    const host = fixture.nativeElement as HTMLElement;

    // Dead letters: the failing queue with its age, the empty one left out; the card is marked.
    expect(text(fixture, '[data-job="dead-letters"] app-status-chip')).toContain('3 mesaj');
    expect(text(fixture, '[data-queue="receipt-consumer_error"]')).toContain('3 saat əvvəl');
    expect(host.querySelector('[data-queue="refund-consumer_error"]')).toBeNull();
    expect(host.querySelector('[data-job="dead-letters"]')!.classList).toContain('jobs__card--bad');
    expect(text(fixture, '[data-job="dead-letters"]')).toContain('Incident-lərə bax');

    // File cleanup in trial mode: says so, and lists what it would delete.
    expect(text(fixture, '[data-job="file-cleanup"] app-status-chip')).toContain('Sınaq rejimi');
    expect(text(fixture, '[data-job="file-cleanup"]')).toContain('heç nə silinmir');
    expect(text(fixture, '[data-job="file-cleanup"] [data-metric="deleted"]')).toContain('Silinəcək');
    expect(text(fixture, '[data-job="file-cleanup"]')).toContain('files/aa/bb/print-v1.pdf');

    // The ledger: the account by its PublicId, linked, with the difference.
    const account = host.querySelector('[data-account="u-1"]')!;
    expect(account.querySelector('a')!.textContent).toContain('NP-ABC123');
    expect(account.textContent).toContain('2,00');

    // The money check: counts by kind, in words.
    expect(text(fixture, '[data-metric="paidNotPrinted"]')).toContain('Ödənilib, çap və refund yoxdur');
    expect(text(fixture, '[data-job="payment-consistency"] app-status-chip')).toContain('1 uyğunsuzluq');

    // Kiosk analytics: a warning, not an incident.
    expect(text(fixture, '[data-job="analytics"] .neo-notice')).toContain('pilot hesabatı natamam ola bilər');
    expect(text(fixture, '[data-kiosk="KSK-9002"]')).toContain('brand_new_thing');
  });

  it('says a check has not run and that every queue is empty', async () => {
    const fixture = await open(jobs({
      runs: {
        'dead-letters': run({ queues: [{ name: 'x_error', messages: 0 }], counts: { messages: 0 } }),
        'wallet-ledger': run({ counts: { checked: 3, mismatches: 0 } }),
      },
      analytics: { today: 0, week: 0, kiosks: [] },
    }));

    expect(text(fixture, '[data-job="dead-letters"]')).toContain('Bütün növbələr boşdur');
    expect(text(fixture, '[data-job="file-cleanup"]')).toContain('Hələ işləməyib');
    expect(text(fixture, '[data-job="wallet-ledger"] app-status-chip')).toContain('Qaydasındadır');
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-job="analytics"] .neo-notice')).toBeNull();
  });

  it('says when the broker could not be read', async () => {
    const failed = await open(jobs({ runs: { 'dead-letters': run({ succeeded: false, error: 'rabbitmq management: HttpRequestException' }) } }));
    expect(text(failed, '[data-job="dead-letters"] .neo-notice')).toContain('mesaj brokeri cavab vermir');
    expect(text(failed, '[data-job="dead-letters"] app-status-chip')).toContain('Yoxlama alınmadı');
  });

  it('offers the Hangfire panel to a SuperAdmin only, and asks for its ticket first', async () => {
    const plain = await open(jobs(), ['Admin']);
    expect((plain.nativeElement as HTMLElement).querySelector('.jobs__hangfire')).toBeNull();
    TestBed.resetTestingModule();

    const fixture = await open(jobs());
    const tab = { opener: {}, location: { href: '' }, close: vi.fn() };
    vi.spyOn(window, 'open').mockReturnValue(tab as unknown as Window);

    ((fixture.nativeElement as HTMLElement).querySelector('.jobs__hangfire') as HTMLButtonElement).click();
    backend.expectOne((request) => request.method === 'POST' && request.url === '/api/admin/hangfire-session').flush(null, { status: 204, statusText: 'No Content' });
    await settle(fixture);

    expect(tab.location.href).toBe('/api/admin/hangfire');
    expect(tab.opener).toBeNull();
  });

  it('follows the incidents the checks raise and close, without a reload', async () => {
    const fixture = await open(jobs());
    vi.useFakeTimers();

    hub.emit('IncidentResolved', { incidentId: 'i-1' });
    await vi.advanceTimersByTimeAsync(liveDebounceMs + 10);
    backend.expectOne('/api/admin/system/jobs').flush(jobs({ analytics: { today: 0, week: 9, kiosks: [] } }));
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('[data-job="analytics"] .neo-notice')).toBeNull();
  });

  it('says what went wrong when the page may not be read', async () => {
    setUp();
    const fixture = TestBed.createComponent(SystemJobsComponent);
    fixture.detectChanges();
    backend.expectOne('/api/admin/system/jobs').flush({ title: 'Forbidden' }, { status: 403, statusText: 'Forbidden' });
    await settle(fixture);

    expect((fixture.nativeElement as HTMLElement).querySelector('app-error-banner')).not.toBeNull();
    expect(text(fixture)).toContain('İşlərin vəziyyəti göstərilə bilmir');
  });
});
