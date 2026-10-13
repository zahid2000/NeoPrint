import { DOCUMENT } from '@angular/common';
import { DestroyRef, inject } from '@angular/core';

import { AdminHubService, ConnectionState } from './admin-hub.service';
import { HubEvent } from './hub-events';

/** Events that arrive in a burst - a status, then its incident - cost one fetch. */
export const liveDebounceMs = 800;

/** How often a page asks again while there is no live connection. */
export const livePollMs = 30_000;

export interface LiveRefreshOptions {
  /** Hub events after which the data on the page is out of date. */
  readonly events: readonly HubEvent[];

  /**
   * Fetches the data again, quietly: what is on screen stays until the answer
   * comes, and a failed attempt shows nothing - the connection indicator in
   * the header already says the panel is cut off.
   */
  readonly reload: () => unknown;

  /** Whether an event concerns this page; without it every listed event does. */
  readonly accepts?: (event: HubEvent, payload: unknown) => boolean;
}

/**
 * Keeps a page's data current without a reload button (T-248).
 *
 * A page that shows something the server changes on its own - a kiosk's
 * status, a queue, a refund - calls this once from its constructor. The data
 * is then fetched again:
 *
 * - shortly after any of the listed hub events;
 * - when the live connection comes back, because events sent while it was
 *   away were not heard;
 * - every thirty seconds for as long as there is no live connection, so a
 *   panel behind a proxy that blocks WebSockets still follows, only slower;
 * - when the tab is looked at again after a while: a browser may put a tab
 *   in the background to sleep, and its connection with it.
 *
 * Everything is undone when the page goes.
 */
export function liveRefresh(options: LiveRefreshOptions): void {
  const hub = inject(AdminHubService);
  const view = inject(DOCUMENT);

  let debounce: ReturnType<typeof setTimeout> | undefined;
  let poll: ReturnType<typeof setInterval> | undefined;
  let hiddenAt: number | null = null;

  const reload = (): void => {
    clearTimeout(debounce);
    debounce = undefined;
    void options.reload();
  };

  const soon = (): void => {
    clearTimeout(debounce);
    debounce = setTimeout(reload, liveDebounceMs);
  };

  const follow = (state: ConnectionState): void => {
    if (state === 'connected') {
      clearInterval(poll);
      poll = undefined;
    } else {
      poll ??= setInterval(reload, livePollMs);
    }
  };

  const onVisibility = (): void => {
    if (view.visibilityState === 'hidden') {
      hiddenAt = Date.now();
    } else if (hiddenAt !== null) {
      const away = Date.now() - hiddenAt;
      hiddenAt = null;

      if (away >= livePollMs) {
        reload();
      }
    }
  };

  const off = [
    ...options.events.map((event) =>
      hub.on<unknown>(event, (payload) => {
        if (!options.accepts || options.accepts(event, payload)) {
          soon();
        }
      }),
    ),
    hub.onState(follow),
    hub.onReconnect(reload),
  ];

  follow(hub.connectionState());
  view.addEventListener('visibilitychange', onVisibility);

  inject(DestroyRef).onDestroy(() => {
    off.forEach((unsubscribe) => unsubscribe());
    clearTimeout(debounce);
    clearInterval(poll);
    view.removeEventListener('visibilitychange', onVisibility);
  });
}
