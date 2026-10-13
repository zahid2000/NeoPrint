import { Injectable, effect, inject, untracked } from '@angular/core';

import { AuthService } from '../../core/auth/auth.service';

/** An answer kept from the last time it was fetched. */
export interface Cached<T> {
  readonly value: T;

  /** When the server gave it, in epoch milliseconds. */
  readonly at: number;
}

const prefix = 'neoprint.operator.';

/**
 * The last answer of each operator screen, kept on the phone (plan section 8B:
 * "zəif internet üçün son yüklənmiş vəziyyət keşdən göstərilir").
 *
 * Kept by the panel itself rather than by the service worker's data cache: a
 * cache keyed by address alone would show the next person who signs in on the
 * same phone what the last one was looking at. Every entry here carries the
 * account it belongs to, an entry of another account is never returned, and
 * all of it is thrown away when the account signs out or changes.
 *
 * What is kept is what the screens show - kiosk names, counters, task notes.
 * No token is ever written here: the access token lives in memory only.
 */
@Injectable({ providedIn: 'root' })
export class OperatorCacheService {
  private readonly auth = inject(AuthService);

  private account = this.auth.admin()?.id ?? null;

  constructor() {
    // Signed out, or somebody else signed in: nothing of the last account stays.
    effect(() => {
      const account = this.auth.admin()?.id ?? null;

      if (account !== this.account) {
        this.account = account;
        untracked(() => this.clear());
      }
    });
  }

  read<T>(key: string): Cached<T> | null {
    const account = this.auth.admin()?.id;

    if (!account) {
      return null;
    }

    try {
      const stored = JSON.parse(localStorage.getItem(prefix + key) ?? 'null') as {
        account?: string;
        at?: number;
        value?: T;
      } | null;

      return stored && stored.account === account && typeof stored.at === 'number'
        ? { value: stored.value as T, at: stored.at }
        : null;
    } catch {
      // Unreadable is the same as not there.
      return null;
    }
  }

  write<T>(key: string, value: T, at = Date.now()): void {
    const account = this.auth.admin()?.id;

    if (!account) {
      return;
    }

    try {
      localStorage.setItem(prefix + key, JSON.stringify({ account, at, value }));
    } catch {
      // A full or forbidden storage costs the offline view, nothing else.
    }
  }

  /** Everything the panel kept, of whichever account. */
  clear(): void {
    try {
      Object.keys(localStorage)
        .filter((key) => key.startsWith(prefix))
        .forEach((key) => localStorage.removeItem(key));
    } catch {
      // Nothing to clear where nothing could be kept.
    }
  }
}
