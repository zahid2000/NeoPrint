import { provideHttpClient } from '@angular/common/http';
import { HttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import {
  EnvironmentInjector,
  createEnvironmentInjector,
  runInInjectionContext,
  signal,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { AuthService } from '../../core/auth/auth.service';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { FakeHub } from '../../core/realtime/fake-hub.testing';
import { liveDebounceMs, livePollMs } from '../../core/realtime/live-refresh';
import { OperatorCacheService } from './operator-cache.service';
import { OperatorScreen, operatorScreen } from './operator-screen';

/**
 * How an operator screen holds its data (plan section 8B, "zəif internet"):
 * what was kept is shown at once, the server is asked behind it, and without
 * the server the screen says how old it is and offers no action.
 */
describe('operatorScreen', () => {
  let backend: HttpTestingController;
  let hub: FakeHub;
  let account: ReturnType<typeof signal<{ id: string } | null>>;
  let page: EnvironmentInjector | null = null;
  let fresh: string[][];

  /** A page opens: a screen is made the way a component's constructor makes one. */
  function open(): OperatorScreen<string[]> {
    page = createEnvironmentInjector([], TestBed.inject(EnvironmentInjector));

    return runInInjectionContext(page, () => {
      const http = TestBed.inject(HttpClient);

      return operatorScreen<string[]>({
        key: () => 'things',
        fetch: () => http.get<string[]>('/api/things'),
        events: ['KioskStatusChanged'],
        fresh: (value) => fresh.push(value),
      });
    });
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
    localStorage.clear();
    hub = new FakeHub();
    account = signal<{ id: string } | null>({ id: 'a-1' });
    fresh = [];

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
    localStorage.clear();
  });

  it('shows the answer, says when it was given, and lets the operator act', async () => {
    const screen = open();
    const asked = screen.reload();

    expect(screen.loading(), 'nothing to show yet').toBe(true);
    expect(screen.canAct()).toBe(false);

    backend.expectOne('/api/things').flush(['one']);
    await asked;

    expect(screen.data()).toEqual(['one']);
    expect(screen.at()).not.toBeNull();
    expect(screen.loading()).toBe(false);
    expect(screen.offline()).toBe(false);
    expect(screen.canAct()).toBe(true);
    expect(fresh, 'what the server said is passed on').toEqual([['one']]);
  });

  it('shows what was kept from the last visit at once, and asks behind it', async () => {
    const first = open();
    void first.reload();
    backend.expectOne('/api/things').flush(['kept']);
    await settle();
    close();
    fresh = [];

    const screen = open();
    const asked = screen.reload();

    expect(screen.data(), 'on screen before the server has answered').toEqual(['kept']);
    expect(screen.loading(), 'no skeleton over something that can be read').toBe(false);
    expect(fresh, 'what was kept is not passed on as news').toEqual([]);

    backend.expectOne('/api/things').flush(['new']);
    await asked;

    expect(screen.data()).toEqual(['new']);
    expect(fresh).toEqual([['new']]);
  });

  it('says how old the data is, and offers no action, when the server cannot be reached', async () => {
    const first = open();
    void first.reload();
    backend.expectOne('/api/things').flush(['kept']);
    await settle();
    const keptAt = first.at();
    close();

    const screen = open();
    const asked = screen.reload();
    backend.expectOne('/api/things').error(new ProgressEvent('error'));
    await asked;

    expect(screen.data(), 'still readable').toEqual(['kept']);
    expect(screen.at(), 'the time the server gave it, not now').toBe(keptAt);
    expect(screen.offline()).toBe(true);
    expect(screen.problem(), 'not an error page: there is something to show').toBeNull();
    expect(screen.canAct(), 'an action is only taken against the present').toBe(false);
  });

  it('says why there is nothing to show when nothing was kept either', async () => {
    const screen = open();
    const asked = screen.reload();
    backend.expectOne('/api/things').error(new ProgressEvent('error'));
    await asked;

    expect(screen.data()).toBeNull();
    expect(screen.problem()).not.toBeNull();
    expect(screen.offline()).toBe(false);
  });

  it('asks again after an event, when the connection comes back, and while it is away', async () => {
    const screen = open();
    void screen.reload();
    backend.expectOne('/api/things').flush(['one']);
    await settle();

    hub.emit('KioskStatusChanged', { kioskId: 'k-1', status: 'Offline' });
    vi.advanceTimersByTime(liveDebounceMs);
    backend.expectOne('/api/things').flush(['two']);
    await settle();
    expect(screen.data()).toEqual(['two']);

    hub.drop();
    vi.advanceTimersByTime(livePollMs);
    backend.expectOne('/api/things').error(new ProgressEvent('error'));
    await settle();
    expect(screen.offline()).toBe(true);

    hub.comeBack();
    backend.expectOne('/api/things').flush(['three']);
    await settle();

    expect(screen.data()).toEqual(['three']);
    expect(screen.offline(), 'back to the present').toBe(false);
  });

  it('never shows one account what was kept for another', async () => {
    const first = open();
    void first.reload();
    backend.expectOne('/api/things').flush(['of the first account']);
    await settle();
    close();

    // Somebody else signs in on the same phone.
    account.set({ id: 'a-2' });
    await settle();

    expect(TestBed.inject(OperatorCacheService).read('things'), 'thrown away with the sign-in').toBeNull();

    const screen = open();
    const asked = screen.reload();

    expect(screen.data()).toBeNull();

    backend.expectOne('/api/things').flush(['of the second account']);
    await asked;

    expect(screen.data()).toEqual(['of the second account']);
  });

  it('keeps no token and nothing but what the screens show', async () => {
    const screen = open();
    void screen.reload();
    backend.expectOne('/api/things').flush(['one']);
    await settle();

    const stored = Object.keys(localStorage).map((key) => `${key}=${localStorage.getItem(key)}`);

    expect(stored).toHaveLength(1);
    expect(stored[0]).toContain('neoprint.operator.things');
    expect(stored[0]).toContain('"account":"a-1"');
    expect(stored[0]).not.toMatch(/token|bearer|eyJ/i);
    expect(screen.data()).toEqual(['one']);
  });
});
