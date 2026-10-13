import { DestroyRef, Injectable, effect, inject, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { MoneyApiService } from '../api/money-api.service';
import { IncidentRow } from '../api/step4.models';
import { AuthService } from '../auth/auth.service';
import { HubEvents } from './hub-events';
import { liveRefresh } from './live-refresh';

/** How many of the open incidents the store keeps to show: the most severe, then the newest. */
export const shownIncidents = 5;

/** An incident somebody still has to deal with. */
export const openIncidentStatuses = ['Open', 'Acknowledged'] as const;

/**
 * How many incidents are open, and the first few of them - once for the whole
 * panel (T-253, T-254).
 *
 * The server sorts and counts: an account with a kiosk scope is told only
 * about its own kiosks' incidents, so neither can be worked out here from the
 * events alone. An event therefore says "ask again", and the count on screen
 * is always one the server gave.
 *
 * A page that shows them calls {@link watch} from its constructor, as with
 * `KioskLiveStore`.
 */
@Injectable({ providedIn: 'root' })
export class IncidentLiveStore {
  private readonly api = inject(MoneyApiService);
  private readonly auth = inject(AuthService);

  private readonly count = signal<number | null>(null);
  private readonly first = signal<readonly IncidentRow[]>([]);
  private readonly failure = signal(false);

  /** Open incidents in the account's kiosks; null until the first answer. */
  readonly open = this.count.asReadonly();

  /** The most severe of them, newest first within a severity. */
  readonly recent = this.first.asReadonly();

  /** True when there is nothing to show because the first fetch failed. */
  readonly failed = this.failure.asReadonly();

  private watchers = 0;
  private account = this.auth.admin()?.id ?? null;
  private generation = 0;

  constructor() {
    liveRefresh({
      events: [HubEvents.incidentRaised, HubEvents.incidentResolved],
      reload: () => (this.watchers > 0 ? this.load() : null),
    });

    // Another account signed in on this tab: what the last one saw is not theirs to see.
    effect(() => {
      const account = this.auth.admin()?.id ?? null;

      if (account !== this.account) {
        this.account = account;
        untracked(() => this.forget());
      }
    });
  }

  /** Called from the constructor of a page that shows the incidents. */
  watch(): void {
    this.watchers++;
    inject(DestroyRef).onDestroy(() => this.watchers--);

    void this.load();
  }

  /** Fetches the count and the first few. What is on screen stays until the answer comes. */
  async load(): Promise<void> {
    const generation = this.generation;

    try {
      const page = await firstValueFrom(
        this.api.listIncidents(openIncidentStatuses, [], 1, shownIncidents),
      );

      if (generation !== this.generation) {
        return;
      }

      this.count.set(page.total);
      this.first.set(page.items);
      this.failure.set(false);
    } catch {
      // A count from a minute ago is better than none; only a first fetch that failed says so.
      if (generation === this.generation && this.count() === null) {
        this.failure.set(true);
      }
    }
  }

  private forget(): void {
    this.generation++;
    this.count.set(null);
    this.first.set([]);
    this.failure.set(false);

    if (this.watchers > 0 && this.account) {
      void this.load();
    }
  }
}
