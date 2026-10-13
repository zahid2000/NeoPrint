import { DestroyRef, Injectable, effect, inject, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, KioskListItem, KioskStatus } from '../api/admin-api.service';
import { AuthService } from '../auth/auth.service';
import { Problem, toProblem } from '../../shared/problem';
import { AdminHubService } from './admin-hub.service';
import { HubEvents, KioskStatusChangedEvent } from './hub-events';
import { liveRefresh } from './live-refresh';

/**
 * The kiosks the account sees and the status each is in now - once for the
 * whole panel (T-254).
 *
 * Before this the kiosk list, the dashboard and the kiosk's own page each kept
 * a copy and each applied `KioskStatusChanged` to it; a page that forgot to
 * was a page that showed yesterday's status (T-248). Now the event is applied
 * here and nowhere else, and a page reads {@link kiosks} or {@link statusOf}.
 *
 * A page that shows the list calls {@link watch} from its constructor. While
 * at least one page watches, the list is fetched again after the events that
 * change a row, when the live connection comes back, and every thirty seconds
 * while there is none (`liveRefresh`). While nobody watches nothing is
 * fetched: a status still arrives with its event, and the next page to watch
 * fetches the rest.
 */
@Injectable({ providedIn: 'root' })
export class KioskLiveStore {
  private readonly api = inject(AdminApiService);
  private readonly auth = inject(AuthService);
  private readonly hub = inject(AdminHubService);

  private readonly list = signal<readonly KioskListItem[] | null>(null);
  private readonly statuses = signal<ReadonlyMap<string, KioskStatus>>(new Map());
  private readonly busy = signal(false);
  private readonly failure = signal<Problem | null>(null);

  /** The kiosks, or null until the first answer. */
  readonly kiosks = this.list.asReadonly();

  /** True while a fetch somebody asked for is on its way; a quiet one never sets it. */
  readonly loading = this.busy.asReadonly();

  /** Why there is no list to show; null while there is one. */
  readonly problem = this.failure.asReadonly();

  private watchers = 0;
  private account = this.auth.admin()?.id ?? null;

  /** Goes up when the account changes, so an answer meant for the last one is dropped. */
  private generation = 0;

  constructor() {
    this.hub.on<KioskStatusChangedEvent>(HubEvents.kioskStatusChanged, (event) =>
      this.learn(event.kioskId, event.status as KioskStatus),
    );

    // What else a row shows: its device, its last heartbeat, its paper.
    liveRefresh({
      events: [
        HubEvents.kioskStatusChanged,
        HubEvents.deviceActivated,
        HubEvents.deviceRevoked,
        HubEvents.supplyAlertRaised,
        HubEvents.supplyAlertCleared,
      ],
      reload: () => (this.watchers > 0 ? this.load(true) : null),
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

  /**
   * Called from the constructor of a page that shows the kiosks. What is
   * already known is shown at once, and fetched again behind it: a heartbeat
   * is not an event, so a list kept from an earlier page may be minutes old.
   */
  watch(): void {
    this.watchers++;
    inject(DestroyRef).onDestroy(() => this.watchers--);

    void this.load(this.list() !== null);
  }

  /** The status a kiosk is in now, when the panel has heard of it. */
  statusOf(kioskId: string | null | undefined): KioskStatus | undefined {
    return kioskId ? this.statuses().get(kioskId) : undefined;
  }

  /**
   * A page learned a kiosk's status another way - its own detail request, a
   * command's answer - and says so, so every page shows the same.
   */
  learn(kioskId: string, status: KioskStatus): void {
    if (this.statuses().get(kioskId) !== status) {
      this.statuses.update((known) => new Map(known).set(kioskId, status));
    }

    this.list.update(
      (list) =>
        list?.map((kiosk) =>
          kiosk.id === kioskId && kiosk.status !== status ? { ...kiosk, status } : kiosk,
        ) ?? null,
    );
  }

  /**
   * Fetches the list. A quiet fetch is one nobody asked for - an event, the
   * connection coming back: what is on screen stays until the answer comes,
   * and an answer that does not come changes nothing.
   */
  async load(quiet = false): Promise<void> {
    const generation = this.generation;

    if (!quiet) {
      this.busy.set(true);
      this.failure.set(null);
    }

    try {
      const list = await firstValueFrom(this.api.listKiosks());

      if (generation !== this.generation) {
        return;
      }

      this.list.set(list);
      this.statuses.update((known) => {
        const next = new Map(known);
        list.forEach((kiosk) => next.set(kiosk.id, kiosk.status));

        return next;
      });
      this.failure.set(null);
    } catch (error) {
      if (generation === this.generation && !quiet) {
        this.failure.set(toProblem(error));
      }
    } finally {
      if (!quiet) {
        this.busy.set(false);
      }
    }
  }

  private forget(): void {
    this.generation++;
    this.list.set(null);
    this.statuses.set(new Map());
    this.failure.set(null);
    this.busy.set(false);

    if (this.watchers > 0 && this.account) {
      void this.load();
    }
  }
}
