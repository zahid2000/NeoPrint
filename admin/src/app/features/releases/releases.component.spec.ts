import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { provideTranslateService, TranslateService } from '@ngx-translate/core';
import { of } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { KioskVersion, Release, ReleasesPage, Rollout } from '../../core/api/releases-api.service';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { translations } from '../../core/i18n/translations';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { FakeHub } from '../../core/realtime/fake-hub.testing';
import { liveDebounceMs } from '../../core/realtime/live-refresh';
import { RolloutDialogComponent } from './release-dialogs';
import { ReleasesComponent } from './releases.component';

let backend: HttpTestingController;
let hub: FakeHub;
let dialogAnswer: unknown;

function setUp(permissions: string[] = [Permissions.releasesView, Permissions.releasesManage], extra: unknown[] = []): void {
  hub = new FakeHub();
  dialogAnswer = undefined;

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      { provide: AdminHubService, useValue: hub },
      // The confirmation dialog answers what the test says.
      { provide: MatDialog, useValue: { open: () => ({ afterClosed: () => of(dialogAnswer) }) } },
      {
        provide: AuthService,
        useValue: {
          admin: signal({ id: 'a-1', roles: ['Admin'] }),
          can: (permission: string) => permissions.includes(permission),
          canAny: (list: readonly string[]) => list.some((permission) => permissions.includes(permission)),
        },
      },
      ...(extra as never[]),
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

const release = (overrides: Partial<Release> = {}): Release => ({
  id: 'r-1',
  component: 'App',
  platform: 'linux-x64',
  keySet: 'staging',
  version: '0.8.0',
  minAgentVersion: '0.8.0',
  channel: 'Pilot',
  fileName: 'neoprint-kiosk-app-0.8.0-linux-x64-staging.tar.gz',
  size: 47_000_000,
  sha256: 'ab'.repeat(32),
  keyId: 'e097a6082f6d3545',
  commit: 'b6d4703',
  builtAt: '2026-10-25T08:00:00Z',
  uploadedAt: '2026-10-25T09:00:00Z',
  isWithdrawn: false,
  withdrawnAt: null,
  withdrawReason: null,
  installedOn: 2,
  ...overrides,
});

const rollout = (overrides: Partial<Rollout> = {}): Rollout => ({
  id: 'o-1',
  releaseId: 'r-1',
  component: 'App',
  platform: 'linux-x64',
  version: '0.8.0',
  target: 'Kiosks',
  kioskIds: ['k-1', 'k-2', 'k-3'],
  percent: null,
  channel: null,
  status: 'Active',
  createdAt: '2026-10-25T09:10:00Z',
  endedAt: null,
  reaches: 3,
  urgent: false,
  ...overrides,
});

const kiosk = (overrides: Partial<KioskVersion> = {}): KioskVersion => ({
  kioskId: 'k-1',
  code: 'KSK-9002',
  name: 'Mərkəz',
  platform: 'linux-x64',
  channel: 'Stable',
  appVersion: '0.7.2',
  agentVersion: '0.7.2',
  provisioningVersion: '3',
  updates: [],
  offered: [],
  lastHeartbeatAt: '2026-10-25T09:20:00Z',
  ...overrides,
});

function page(overrides: Partial<ReleasesPage> = {}): ReleasesPage {
  return {
    keySet: 'staging',
    trustedKeys: 2,
    releases: [release(), release({ id: 'r-2', version: '0.7.9', channel: 'Stable', isWithdrawn: true, withdrawReason: 'Çap pozulur' })],
    rollouts: [rollout(), rollout({ id: 'o-2', status: 'RolledBack', target: 'Channel', channel: 'Pilot', kioskIds: [] })],
    ...overrides,
  };
}

async function open(answer: ReleasesPage, kiosks: KioskVersion[] = [kiosk()], permissions?: string[]): Promise<ComponentFixture<ReleasesComponent>> {
  setUp(permissions);
  const fixture = TestBed.createComponent(ReleasesComponent);
  fixture.detectChanges();
  backend.expectOne('/api/admin/releases').flush(answer);
  backend.expectOne('/api/admin/kiosks/versions').flush(kiosks);
  await settle(fixture);
  return fixture;
}

async function tab(fixture: ComponentFixture<ReleasesComponent>, index: number): Promise<void> {
  fixture.componentInstance.tab.set(index);
  fixture.detectChanges();
  // Without a browser's transitions the tab body is attached by Material's own fallback timer (100 ms).
  await new Promise((resolve) => setTimeout(resolve, 150));
  await settle(fixture);
}

function reloads(answer: ReleasesPage = page(), kiosks: KioskVersion[] = [kiosk()]): void {
  backend.expectOne('/api/admin/releases').flush(answer);
  backend.expectOne('/api/admin/kiosks/versions').flush(kiosks);
}

afterEach(() => {
  backend.verify();
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('Buraxılışlar', () => {
  it('says which key set the server takes and lists the releases with channel, signature and withdrawal', async () => {
    const fixture = await open(page());

    expect(text(fixture, '[data-notice="key-set"]')).toContain('Bu server staging paketlərini qəbul edir.');
    expect(text(fixture, '[data-tab="releases"]')).toContain('Kiosk tətbiqi');
    expect(text(fixture, '[data-tab="releases"]')).toContain('Linux');
    expect(text(fixture, '[data-release="r-1"]')).toContain('Pilot');
    expect(text(fixture, '[data-release="r-2"]')).toContain('Geri çəkilib');
    expect(text(fixture, '[data-release="r-2"]')).toContain('Səbəb: Çap pozulur');
    expect(text(fixture, '[data-tab="releases"]')).toContain('Açar e097a6082f6d3545');
    expect(text(fixture, '[data-tab="releases"]')).toContain('2 kiosk');
    // Nothing of the server's own spelling reaches the screen.
    expect(text(fixture, '[data-tab="releases"]')).not.toContain('linux-x64');
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-action="upload"]')).not.toBeNull();
  });

  it('warns that nothing is accepted while the server has no key, and shows an empty list as such', async () => {
    const fixture = await open(page({ trustedKeys: 0, keySet: 'production', releases: [], rollouts: [] }), []);

    expect(text(fixture, '[data-notice="no-keys"]')).toContain('heç bir paket qəbul olunmur');
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-notice="key-set"]')).toBeNull();
    expect(text(fixture, '[data-tab="releases"] app-empty-state')).toContain('Hələ paket yüklənməyib');
  });

  it('offers no upload and no actions to an account that may only look', async () => {
    const fixture = await open(page(), [kiosk()], [Permissions.releasesView]);
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('[data-action="upload"]')).toBeNull();
    expect(host.querySelector('[data-tab="releases"] button[mat-icon-button]')).toBeNull();
  });

  it('lists the rollouts with their target and state, and only the actions their state allows', async () => {
    const fixture = await open(page());
    await tab(fixture, 1);

    expect(text(fixture, '[data-tab="rollouts"]')).toContain('3 kiosk');
    expect(text(fixture, '[data-rollout="o-1"]')).toContain('Aktiv');
    expect(text(fixture, '[data-rollout="o-2"]')).toContain('Geri çəkilib');
    expect(text(fixture, '[data-tab="rollouts"]')).toContain('Pilot kanalı');

    const component = fixture.componentInstance;
    expect(component.actionsOf(rollout())).toEqual(['pause', 'complete', 'rollback']);
    expect(component.actionsOf(rollout({ status: 'Paused' }))).toEqual(['resume', 'complete', 'rollback']);
    expect(component.actionsOf(rollout({ status: 'RolledBack' }))).toEqual([]);
  });

  it('shows each kiosk with its versions, what it is offered and how its last update went', async () => {
    const fixture = await open(page(), [
      kiosk({
        offered: [{ component: 'App', version: '0.8.0' }],
        updates: [{ component: 'App', version: '0.8.0', status: 'Failed', message: 'Paket imza yoxlamasından keçmədi.', reportedAt: '2026-10-25T09:30:00Z' }],
      }),
    ]);
    await tab(fixture, 2);

    expect(text(fixture, '[data-kiosk="k-1"]')).toContain('KSK-9002');
    expect(text(fixture, '[data-tab="kiosks"]')).toContain('0.7.2');
    expect(text(fixture, '[data-tab="kiosks"]')).toContain('Kiosk tətbiqi 0.8.0');
    expect(text(fixture, '[data-update="App"] app-status-chip')).toContain('Alınmadı');
    expect(text(fixture, '[data-update="App"]')).toContain('Paket imza yoxlamasından keçmədi.');
  });

  it('withdraws a release with the reason given and asks the server again', async () => {
    const fixture = await open(page());
    dialogAnswer = { reason: 'Çap pozulur' };

    void fixture.componentInstance.withdraw(release());
    await settle(fixture);

    const request = backend.expectOne((r) => r.method === 'POST' && r.url === '/api/admin/releases/r-1/withdraw');
    expect(request.request.body).toEqual({ reason: 'Çap pozulur' });
    expect(request.request.headers.has('Idempotency-Key')).toBe(true);
    request.flush(release({ isWithdrawn: true }));
    await settle(fixture);
    reloads();
  });

  it('does nothing when the withdrawal is cancelled', async () => {
    const fixture = await open(page());
    dialogAnswer = undefined;

    await fixture.componentInstance.withdraw(release());
    backend.expectNone('/api/admin/releases/r-1/withdraw');
  });

  it('follows releases and the kiosks\' update reports without a reload', async () => {
    const fixture = await open(page());
    vi.useFakeTimers();

    hub.emit('KioskUpdateReported', { kioskId: 'k-1' });
    await vi.advanceTimersByTimeAsync(liveDebounceMs + 10);
    reloads(page({ releases: [] }));
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    expect(text(fixture, '[data-tab="releases"] app-empty-state')).toContain('Hələ paket yüklənməyib');
  });

  it('marks an urgent rollout, and shows a kiosk waiting for an idle moment or turning customers away (T-321)', async () => {
    const fixture = await open(
      { ...page(), rollouts: [rollout({ urgent: true })] },
      [
        kiosk({ updates: [{ component: 'App', version: '0.8.0', status: 'WaitingForIdle', message: null, reportedAt: '2026-10-25T02:05:00Z' }] }),
        kiosk({ kioskId: 'k-2', code: 'KSK-9003', updates: [{ component: 'App', version: '0.8.0', status: 'Draining', message: null, reportedAt: '2026-10-25T02:40:00Z' }] }),
      ],
    );
    await tab(fixture, 1);
    expect(text(fixture, '[data-rollout="o-1"]')).toContain('Təcili');

    await tab(fixture, 2);
    expect(text(fixture, '[data-tab="kiosks"]')).toContain('Boş an gözlənilir');
    expect(text(fixture, '[data-tab="kiosks"]')).toContain('Yeni müştəri qəbul edilmir');
  });

  it('sends an urgent rollout only when it is ticked (T-321)', async () => {
    const closed: unknown[] = [];
    setUp(undefined, [
      { provide: MAT_DIALOG_DATA, useValue: { release: release(), kiosks: [] } },
      { provide: MatDialogRef, useValue: { close: (value: unknown) => closed.push(value) } },
    ]);
    const fixture = TestBed.createComponent(RolloutDialogComponent);
    fixture.detectChanges();
    const dialog = fixture.componentInstance;

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Təcili (təhlükəsizlik) yeniləmə');
    dialog.target.set('pilot');
    dialog.urgent.set(true);
    const created = dialog.create();
    const request = backend.expectOne('/api/admin/rollouts');
    expect(request.request.body).toEqual({ releaseId: 'r-1', target: 'Channel', channel: 'Pilot', urgent: true });
    request.flush(rollout({ urgent: true }));
    await created;

    expect(closed).toHaveLength(1);
  });

  it('says what went wrong when the page may not be read', async () => {
    setUp();
    const fixture = TestBed.createComponent(ReleasesComponent);
    fixture.detectChanges();
    backend.expectOne('/api/admin/releases').flush({ title: 'Forbidden' }, { status: 403, statusText: 'Forbidden' });
    backend.expectOne('/api/admin/kiosks/versions').flush([]);
    await settle(fixture);

    expect((fixture.nativeElement as HTMLElement).querySelector('app-error-banner')).not.toBeNull();
    expect(text(fixture)).toContain('Buraxılışlar göstərilə bilmir');
  });
});
