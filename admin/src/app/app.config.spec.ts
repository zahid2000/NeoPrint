import { ApplicationInitStatus } from '@angular/core';
import { SwUpdate } from '@angular/service-worker';
import { TestBed } from '@angular/core/testing';
import { TranslateService } from '@ngx-translate/core';
import { afterEach, describe, expect, it } from 'vitest';

import { appConfig, newerBuild } from './app.config';
import { AuthService } from './core/auth/auth.service';
import { ThemeService } from './core/theme/theme.service';

/**
 * The app initializer as the browser runs it (T-124): it awaits the
 * dictionary chunk before it is done, so an `inject()` after that await threw
 * NG0203 and left the panel a blank page that every component test missed.
 */
describe('app initializer', () => {
  it('loads the dictionary and restores the session without leaving the injection context', async () => {
    let restored = false;

    TestBed.configureTestingModule({
      providers: [
        ...appConfig.providers,
        { provide: AuthService, useValue: { restore: async () => void (restored = true) } },
        { provide: ThemeService, useValue: { initialise: () => undefined } },
      ],
    });

    await TestBed.inject(ApplicationInitStatus).donePromise;

    expect(restored).toBe(true);
    expect(TestBed.inject(TranslateService).instant('common.reason')).toBe('Səbəb');
  });
});

/** A service worker that never answers - as it is before it has registered. */
function silentUpdates(): { updates: SwUpdate; asked: () => number } {
  let asked = 0;
  const updates = {
    isEnabled: true,
    checkForUpdate: () => {
      asked++;
      return new Promise<boolean>(() => undefined);
    },
    activateUpdate: () => Promise.resolve(true),
  } as unknown as SwUpdate;

  return { updates, asked: () => asked };
}

function serveFrom(controller: object | null): void {
  Object.defineProperty(navigator, 'serviceWorker', { configurable: true, value: { controller } });
}

describe('the start-up look for a newer build (T-211, T-278)', () => {
  afterEach(() => {
    Reflect.deleteProperty(navigator, 'serviceWorker');
    sessionStorage.clear();
  });

  it('does not wait when no service worker serves the page: it came from the server, and is the newest', async () => {
    serveFrom(null);
    const { updates, asked } = silentUpdates();
    const started = Date.now();

    expect(await newerBuild(updates)).toBe(false);
    expect(asked(), 'the worker is not asked at all').toBe(0);
    expect(Date.now() - started).toBeLessThan(500);
  });

  it('asks the service worker that serves the page, and gives up after a while', async () => {
    serveFrom({});
    const { updates, asked } = silentUpdates();

    expect(await newerBuild(updates)).toBe(false);
    expect(asked()).toBe(1);
  });
});
