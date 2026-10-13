import { DOCUMENT } from '@angular/common';
import { DestroyRef, Signal, computed, effect, inject, signal, untracked } from '@angular/core';
import { Observable, firstValueFrom } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { HubEvent } from '../../core/realtime/hub-events';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { Problem, toProblem } from '../../shared/problem';
import { OperatorCacheService } from './operator-cache.service';

export interface OperatorScreenOptions<T> {
  /** Where the answer is kept on the phone; null while there is nothing to ask for yet. */
  readonly key: () => string | null;

  readonly fetch: () => Observable<T>;

  /** Hub events after which what is shown is out of date. */
  readonly events: readonly HubEvent[];

  /** Whether an event concerns this screen; without it every listed event does. */
  readonly accepts?: (event: HubEvent, payload: unknown) => boolean;

  /**
   * Called with every answer the server gives - not with what was kept from an
   * earlier visit, which may be hours old and must not be passed on as news.
   */
  readonly fresh?: (value: T) => void;
}

/** What an operator screen shows, and how fresh it is. */
export interface OperatorScreen<T> {
  /** The last answer - from the server, or kept from an earlier visit. */
  readonly data: Signal<T | null>;

  /** When the server gave it, in epoch milliseconds. */
  readonly at: Signal<number | null>;

  /** Nothing to show yet and the first answer is on its way. */
  readonly loading: Signal<boolean>;

  /** The last attempt failed: what is shown is from {@link at}, and nothing can be done until it works again. */
  readonly offline: Signal<boolean>;

  /** Why there is nothing to show at all. */
  readonly problem: Signal<Problem | null>;

  /** Whether an action may be sent: only against what the server said just now. */
  readonly canAct: Signal<boolean>;

  /** Asks again. Quiet when something is already on screen. */
  reload(): Promise<void>;

  /** An action's own answer already says what the screen should show. */
  show(value: T): void;
}

/**
 * The way every operator screen holds its data (plan section 8B, "zəif
 * internet"): what was kept from the last visit is on screen at once, the
 * server is asked behind it, and when the server cannot be reached the screen
 * says how old what it shows is - and offers no action, because an action is
 * only ever taken against the present.
 *
 * Called once from a component's constructor; follows the hub like every live
 * page (`liveRefresh`).
 */
export function operatorScreen<T>(options: OperatorScreenOptions<T>): OperatorScreen<T> {
  const cache = inject(OperatorCacheService);

  const data = signal<T | null>(null);
  const at = signal<number | null>(null);
  const loading = signal(false);
  const offline = signal(false);
  const problem = signal<Problem | null>(null);

  let shownKey: string | null = null;
  let asked = 0;

  // Somebody else signed in on this phone: what the last account was shown is
  // not theirs to see, and an answer still on its way is not shown to them.
  const auth = inject(AuthService);
  let account = auth.admin()?.id ?? null;

  effect(() => {
    const now = auth.admin()?.id ?? null;

    if (now !== account) {
      account = now;
      untracked(() => {
        asked++;
        shownKey = null;
        data.set(null);
        at.set(null);
        offline.set(false);
        problem.set(null);
        loading.set(false);
      });
    }
  });

  async function reload(): Promise<void> {
    const key = options.key();

    if (!key) {
      return;
    }

    // Another kiosk, another task list: what was on screen is not this one's.
    if (key !== shownKey) {
      const kept = cache.read<T>(key);

      shownKey = key;
      data.set(kept?.value ?? null);
      at.set(kept?.at ?? null);
      offline.set(false);
      problem.set(null);
    }

    const mine = ++asked;
    loading.set(data() === null);

    try {
      const value = await firstValueFrom(options.fetch());

      // A later question was asked meanwhile; its answer is the one to show.
      if (mine !== asked || options.key() !== key) {
        return;
      }

      show(value);
    } catch (error) {
      if (mine !== asked || options.key() !== key) {
        return;
      }

      if (data() === null) {
        problem.set(toProblem(error));
      } else {
        offline.set(true);
      }
    } finally {
      if (mine === asked) {
        loading.set(false);
      }
    }
  }

  function show(value: T): void {
    const key = options.key();
    const now = Date.now();

    data.set(value);
    at.set(now);
    offline.set(false);
    problem.set(null);

    if (key) {
      cache.write(key, value, now);
    }

    options.fresh?.(value);
  }

  liveRefresh({ events: options.events, accepts: options.accepts, reload });

  // The device itself says when it has no network at all: that is heard at
  // once, without waiting for a request to fail. Having one again is only a
  // reason to ask - whether the server answers is still to be seen.
  const view = inject(DOCUMENT).defaultView;
  const wentOffline = (): void => {
    if (data() !== null) {
      offline.set(true);
    }
  };
  const cameBack = (): void => void reload();

  view?.addEventListener('offline', wentOffline);
  view?.addEventListener('online', cameBack);

  inject(DestroyRef).onDestroy(() => {
    view?.removeEventListener('offline', wentOffline);
    view?.removeEventListener('online', cameBack);
  });

  return {
    data: data.asReadonly(),
    at: at.asReadonly(),
    loading: loading.asReadonly(),
    offline: offline.asReadonly(),
    problem: problem.asReadonly(),
    canAct: computed(() => data() !== null && !offline()),
    reload,
    show,
  };
}
