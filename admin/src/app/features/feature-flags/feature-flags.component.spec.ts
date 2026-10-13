import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { describe, beforeEach, afterEach, expect, it, vi } from 'vitest';

import { FeatureFlagDetails, KioskListItem } from '../../core/api/admin-api.service';
import { supportedLanguages, translations } from '../../core/i18n/translations';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { FakeHub } from '../../core/realtime/fake-hub.testing';
import { liveDebounceMs } from '../../core/realtime/live-refresh';
import { FeatureFlagsComponent } from './feature-flags.component';

const kioskA = '11111111-1111-1111-1111-111111111111';
const kioskB = '22222222-2222-2222-2222-222222222222';

const kiosks: KioskListItem[] = [
  {
    id: kioskA,
    code: 'KSK-0001',
    name: 'Central',
    status: 'Online',
    platform: 'Windows',
    address: 'Baku',
    defaultLanguage: 'az',
    activeDeviceId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
    appVersion: '0.1.0',
    selectedPrinter: 'HP LaserJet',
    lastHeartbeatAt: new Date().toISOString(),
  },
  {
    id: kioskB,
    code: 'KSK-0002',
    name: 'Airport',
    status: 'NotActivated',
    platform: 'Unknown',
    address: 'Baku',
    defaultLanguage: 'az',
    activeDeviceId: null,
    appVersion: null,
    selectedPrinter: null,
    lastHeartbeatAt: null,
  },
];

const flags: FeatureFlagDetails[] = [
  {
    id: '33333333-3333-3333-3333-333333333333',
    key: 'scan.enabled',
    description: 'Scanning at the kiosk',
    defaultEnabled: false,
    platforms: ['Kiosk'],
    kioskOverrides: { [kioskA]: true },
    userAllowlist: [],
    rolloutPercent: 0,
    updatedAt: new Date().toISOString(),
  },
  {
    id: '44444444-4444-4444-4444-444444444444',
    key: 'wallet.enabled',
    description: null,
    defaultEnabled: true,
    platforms: [],
    kioskOverrides: {},
    userAllowlist: ['55555555-5555-5555-5555-555555555555'],
    rolloutPercent: 50,
    updatedAt: new Date().toISOString(),
  },
];

async function flushMicrotasks(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

describe('FeatureFlagsComponent', () => {
  let fixture: ComponentFixture<FeatureFlagsComponent>;
  let component: FeatureFlagsComponent;
  let backend: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'login', children: [] }]),
        provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(FeatureFlagsComponent);
    component = fixture.componentInstance;

    backend.expectOne('/api/admin/features').flush(flags);

    await flushMicrotasks();

    backend.expectOne('/api/admin/kiosks').flush(kiosks);

    await fixture.whenStable();
  });

  afterEach(() => {
    backend.verify();
  });

  it('starts with a draft that matches the server', () => {
    expect(component.flags()).toHaveLength(2);
    expect(component.isDirty(flags[0])).toBe(false);
    expect(component.isDirty(flags[1])).toBe(false);
  });

  it('reads the per-kiosk override state', () => {
    expect(component.overrideState('scan.enabled', kioskA)).toBe('on');
    expect(component.overrideState('scan.enabled', kioskB)).toBe('default');
  });

  it('cycles a kiosk through on, off and following the default', () => {
    expect(component.overrideState('scan.enabled', kioskB)).toBe('default');

    component.cycleOverride('scan.enabled', kioskB);
    expect(component.overrideState('scan.enabled', kioskB)).toBe('on');

    component.cycleOverride('scan.enabled', kioskB);
    expect(component.overrideState('scan.enabled', kioskB)).toBe('off');

    component.cycleOverride('scan.enabled', kioskB);
    expect(component.overrideState('scan.enabled', kioskB)).toBe('default');
  });

  it('drops the override entirely rather than storing a false one', () => {
    component.cycleOverride('scan.enabled', kioskA);
    expect(component.overrideState('scan.enabled', kioskA)).toBe('off');

    component.cycleOverride('scan.enabled', kioskA);

    // Back to no entry at all: an explicit false would keep the kiosk out of a
    // later rollout, which is not what "follows the default" means.
    expect(component.overriddenKioskIds('scan.enabled')).toEqual([]);
  });

  it('notices an unsaved change and reverts it', () => {
    component.setDefault('scan.enabled', true);

    expect(component.isDirty(flags[0])).toBe(true);

    component.revert(flags[0]);

    expect(component.isDirty(flags[0])).toBe(false);
    expect(component.draft('scan.enabled').defaultEnabled).toBe(false);
  });

  it('keeps the rollout inside nought to a hundred', () => {
    component.setRollout('wallet.enabled', 140);
    expect(component.draft('wallet.enabled').rolloutPercent).toBe(100);

    component.setRollout('wallet.enabled', -20);
    expect(component.draft('wallet.enabled').rolloutPercent).toBe(0);
  });

  it('sends the whole flag, allowlist included, when saving', async () => {
    component.setDefault('scan.enabled', true);
    component.setRollout('scan.enabled', 25);
    component.cycleOverride('scan.enabled', kioskB);

    const saved = component.save(flags[0]);

    const request = backend.expectOne('/api/admin/features/scan.enabled');

    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      defaultEnabled: true,
      rolloutPercent: 25,
      kioskOverrides: { [kioskA]: true, [kioskB]: true },
      // Carried through untouched: this screen does not edit the allowlist, and
      // leaving it out would clear it.
      userAllowlist: [],
    });

    request.flush({
      ...flags[0],
      defaultEnabled: true,
      rolloutPercent: 25,
      kioskOverrides: { [kioskA]: true, [kioskB]: true },
    });

    await saved;

    expect(component.isDirty(component.flags()[0])).toBe(false);
  });

  it('shows the kiosk code rather than its id once the list is loaded', () => {
    expect(component.kioskLabel(kioskA)).toBe('KSK-0001');
    expect(component.kioskLabel('66666666-6666-6666-6666-666666666666')).toBe(
      '66666666-6666-6666-6666-666666666666',
    );
  });

  it('falls back to the technical key for a flag this build has no name for', () => {
    expect(component.hasText(flags[0])).toBe(false);
    expect(component.hasText({ ...flags[0], key: 'referral' })).toBe(true);
  });
});

/**
 * Somebody else saved a flag while this page was open (T-253). The page may
 * hold changes that are not saved, so it does not refresh by itself: it says
 * so, and "Yenilə" takes the new values without losing what was being changed.
 */
describe('FeatureFlagsComponent, a flag changed elsewhere', () => {
  let fixture: ComponentFixture<FeatureFlagsComponent>;
  let component: FeatureFlagsComponent;
  let backend: HttpTestingController;
  let hub: FakeHub;

  const notice = (): HTMLElement | null => {
    fixture.detectChanges();

    return (fixture.nativeElement as HTMLElement).querySelector('.neo-notice');
  };

  async function settle(): Promise<void> {
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
  }

  /** The server announces a change; the page asks what the flags are now. */
  async function announce(now: FeatureFlagDetails[]): Promise<void> {
    hub.emit('FeatureFlagsChanged');
    vi.advanceTimersByTime(liveDebounceMs);
    backend.expectOne('/api/admin/features').flush(now);
    await settle();
  }

  beforeEach(async () => {
    vi.useFakeTimers();
    hub = new FakeHub();

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'login', children: [] }]),
        provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
        { provide: AdminHubService, useValue: hub },
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(FeatureFlagsComponent);
    component = fixture.componentInstance;

    backend.expectOne('/api/admin/features').flush(flags);
    await settle();
    backend.expectOne('/api/admin/kiosks').flush(kiosks);
    await settle();
  });

  afterEach(() => {
    fixture.destroy();
    backend.verify();
    vi.useRealTimers();
  });

  it('says so, and changes nothing on the page', async () => {
    component.setRollout('scan.enabled', 40);
    expect(notice()).toBeNull();

    await announce([flags[0], { ...flags[1], defaultEnabled: false }]);

    expect(notice(), 'the notice is on screen').not.toBeNull();
    expect(component.flags()[1].defaultEnabled, 'the page still shows what it had').toBe(true);
    expect(component.draft('scan.enabled').rolloutPercent, 'the unsaved change is still there').toBe(40);
  });

  it('takes the new values on "Yenilə" and keeps what was being changed here', async () => {
    component.setRollout('scan.enabled', 40);

    await announce([
      { ...flags[0], defaultEnabled: true },
      { ...flags[1], defaultEnabled: false, rolloutPercent: 10 },
    ]);

    notice()!.querySelector('button')!.click();
    fixture.detectChanges();

    expect(notice()).toBeNull();

    // The flag nobody touched here follows the server ...
    expect(component.flags()[1].defaultEnabled).toBe(false);
    expect(component.draft('wallet.enabled').rolloutPercent).toBe(10);
    expect(component.isDirty(component.flags()[1])).toBe(false);

    // ... and the one being changed keeps what was entered, now against the new server value.
    expect(component.draft('scan.enabled').rolloutPercent).toBe(40);
    expect(component.flags()[0].defaultEnabled).toBe(true);
    expect(component.isDirty(component.flags()[0])).toBe(true);
  });

  it('says nothing about its own save', async () => {
    component.setDefault('scan.enabled', true);

    const saving = component.save(flags[0]);
    const request = backend.expectOne('/api/admin/features/scan.enabled');

    // The announcement of this very save arrives before its answer.
    hub.emit('FeatureFlagsChanged');
    vi.advanceTimersByTime(liveDebounceMs);
    backend.expectNone('/api/admin/features');

    request.flush({ ...flags[0], defaultEnabled: true });
    await saving;
    await settle();

    // Asked once the save is through: the server holds what the page holds.
    vi.advanceTimersByTime(liveDebounceMs);
    backend.expectOne('/api/admin/features').flush([{ ...flags[0], defaultEnabled: true }, flags[1]]);
    await settle();

    expect(notice()).toBeNull();
  });

  it('takes the notice away when the page is reloaded by hand', async () => {
    await announce([flags[0], { ...flags[1], rolloutPercent: 5 }]);
    expect(notice()).not.toBeNull();

    const reloading = component.reload();
    backend.expectOne('/api/admin/features').flush([flags[0], { ...flags[1], rolloutPercent: 5 }]);
    await settle();
    backend.expectOne('/api/admin/kiosks').flush(kiosks);
    await reloading;

    expect(notice()).toBeNull();
    expect(component.draft('wallet.enabled').rolloutPercent).toBe(5);
  });
});

// The MVP flags the backend seeds (FeatureFlag.MvpFlags): each needs a human name
// and a one-sentence explanation in every language, or the page shows a bare key.
const mvpFlagKeys = [
  'mobile_payment_at_kiosk',
  'user_qr_print',
  'mobile_session_print',
  'scheduled_print',
  'file_sharing',
  'referral',
  'promo_codes',
  'layout_editor',
  'document_scan',
  'txt_editor',
  'data_export',
];

describe('feature flag names', () => {
  for (const language of supportedLanguages) {
    it(`names and explains every MVP flag in ${language}`, () => {
      for (const key of mvpFlagKeys) {
        expect(translations[language][`features.flag.${key}.name`], key).toBeTruthy();
        expect(translations[language][`features.flag.${key}.description`], key).toBeTruthy();
      }
    });
  }
});
