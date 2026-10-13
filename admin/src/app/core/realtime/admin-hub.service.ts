import { DestroyRef, Injectable, InjectionToken, inject, signal } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  IRetryPolicy,
  LogLevel,
  RetryContext,
} from '@microsoft/signalr';

import { AuthService } from '../auth/auth.service';
import { HubEvent } from './hub-events';

/**
 * Makes the SignalR connection builder.
 *
 * A token rather than a `new` in the service, so a test hands in a fake
 * connection through dependency injection instead of mocking the module - a
 * module mock breaks as soon as the service is shared with another spec's
 * bundle.
 */
export const HUB_CONNECTION_BUILDER = new InjectionToken<() => HubConnectionBuilder>(
  'HUB_CONNECTION_BUILDER',
  { providedIn: 'root', factory: () => () => new HubConnectionBuilder() },
);

/** What the panel shows about its own link to the server. */
export type ConnectionState = 'connected' | 'reconnecting' | 'disconnected';

/**
 * Reconnects for as long as the panel is open.
 *
 * SignalR's default policy gives up after about half a minute, which is the
 * wrong behaviour for a screen somebody leaves open all day: the API being
 * restarted or a laptop waking from sleep would leave the panel silently
 * stale. The delay climbs to half a minute and stays there.
 */
export class ForeverRetryPolicy implements IRetryPolicy {
  nextRetryDelayInMilliseconds(context: RetryContext): number {
    const steps = [0, 2_000, 5_000, 10_000, 30_000];

    return steps[Math.min(context.previousRetryCount, steps.length - 1)];
  }
}

/**
 * The panel's connection to `/hubs/admin` (plan section 6).
 *
 * Nothing in the panel subscribes to the connection directly: a feature calls
 * {@link on} with the event it cares about and gets a function that removes
 * the handler again. That keeps one connection for the whole application
 * rather than one per page, which is what the hub's group membership assumes.
 *
 * The access token lives fifteen minutes, and the server ends the connection
 * when the token it was opened with runs out - the kiosks an account hears
 * about are decided at that moment (T-249). So `accessTokenFactory` is asked
 * on every (re)connect, and what it hands over is a token that is good now.
 *
 * An event is only heard while the connection is up. A feature that shows
 * live data therefore also listens for {@link onReconnect} and fetches its
 * data again - through `liveRefresh`, which does that and the rest.
 */
@Injectable({ providedIn: 'root' })
export class AdminHubService {
  private readonly auth = inject(AuthService);
  private readonly createBuilder = inject(HUB_CONNECTION_BUILDER);

  private connection: HubConnection | null = null;

  /** Handlers by event name, so several features can watch the same event. */
  private readonly handlers = new Map<string, Set<(payload: unknown) => void>>();

  private readonly state = signal<ConnectionState>('disconnected');

  private readonly stateListeners = new Set<(state: ConnectionState) => void>();
  private readonly reconnectListeners = new Set<() => void>();

  /** What to show next to live data: connected, reconnecting or offline. */
  readonly connectionState = this.state.asReadonly();

  /** When the last event arrived, for the "last updated" label. */
  private readonly lastEvent = signal<Date | null>(null);

  readonly lastEventAt = this.lastEvent.asReadonly();

  constructor() {
    // The panel is a single-page application: the connection is closed when
    // the root injector goes, not when a page navigates away.
    inject(DestroyRef).onDestroy(() => void this.stop());
  }

  /**
   * Opens the connection, or does nothing if it is already open.
   *
   * Called from the shell once the administrator is signed in: connecting
   * before that would only be refused, since the hub requires the admin
   * policy.
   */
  async start(): Promise<void> {
    if (this.connection) {
      return;
    }

    const connection = this.createBuilder()
      .withUrl('/hubs/admin', {
        // Asked per connection attempt, not captured once: the access token
        // rotates every fifteen minutes, and one that has run out is
        // exchanged before it is handed over.
        accessTokenFactory: () => this.auth.freshToken(),
      })
      .withAutomaticReconnect(new ForeverRetryPolicy())
      .configureLogging(LogLevel.Warning)
      .build();

    connection.onreconnecting(() => this.setState('reconnecting'));
    connection.onreconnected(() => this.connectedAgain());
    connection.onclose(() => this.setState('disconnected'));

    this.connection = connection;

    // Handlers registered before the connection existed are attached now.
    for (const event of this.handlers.keys()) {
      this.attach(event);
    }

    try {
      await connection.start();
      this.setState('connected');
    } catch {
      // A failed first connect is not an error worth showing: the panel works
      // without real-time, it just does not update on its own. The indicator
      // says so, and the retry below picks it up.
      this.setState('disconnected');
      this.scheduleRetry();
    }
  }

  /**
   * Closes the connection and opens a new one.
   *
   * The server decides which kiosks a connection hears about when it opens,
   * so a change to the account's scope takes a new connection to apply.
   */
  async restart(): Promise<void> {
    if (!this.connection) {
      return;
    }

    await this.stop();
    await this.start();

    if (this.state() === 'connected') {
      this.announceReconnect();
    }
  }

  /**
   * Calls the listener whenever the connection state changes, and returns
   * the function that removes it. A plain callback next to the signal, for
   * code that starts and stops timers rather than renders.
   */
  onState(listener: (state: ConnectionState) => void): () => void {
    this.stateListeners.add(listener);

    return () => {
      this.stateListeners.delete(listener);
    };
  }

  /**
   * Calls the listener each time the connection comes back after having been
   * away - events sent in between were not heard, so what is on screen has to
   * be fetched again. Not called for the first connect of a session.
   */
  onReconnect(listener: () => void): () => void {
    this.reconnectListeners.add(listener);

    return () => {
      this.reconnectListeners.delete(listener);
    };
  }

  /**
   * Subscribes to an event and returns the unsubscribe function.
   *
   * The event is one of `HubEvents` - the one list of what the server sends
   * (T-252). The payload is typed by the caller, which knows its shape.
   */
  on<T>(event: HubEvent, handler: (payload: T) => void): () => void {
    const wrapped = (payload: unknown) => {
      this.lastEvent.set(new Date());
      handler(payload as T);
    };

    let handlers = this.handlers.get(event);

    if (!handlers) {
      handlers = new Set();
      this.handlers.set(event, handlers);
      this.attach(event);
    }

    handlers.add(wrapped);

    return () => {
      handlers!.delete(wrapped);
    };
  }

  /** Closes the connection. */
  async stop(): Promise<void> {
    const connection = this.connection;
    this.connection = null;

    if (connection && connection.state !== HubConnectionState.Disconnected) {
      await connection.stop();
    }

    this.setState('disconnected');
  }

  private setState(next: ConnectionState): void {
    if (this.state() === next) {
      return;
    }

    this.state.set(next);

    for (const listener of [...this.stateListeners]) {
      listener(next);
    }
  }

  private connectedAgain(): void {
    this.setState('connected');
    this.announceReconnect();
  }

  private announceReconnect(): void {
    for (const listener of [...this.reconnectListeners]) {
      // One page failing to refresh must not keep the others stale.
      try {
        listener();
      } catch {
        // Deliberately swallowed - see above.
      }
    }
  }

  /**
   * Routes one hub event to every handler registered for it.
   *
   * Attached once per event name rather than once per handler, so adding a
   * second listener does not register a second SignalR callback.
   */
  private attach(event: string): void {
    this.connection?.on(event, (payload: unknown) => {
      for (const handler of this.handlers.get(event) ?? []) {
        // One handler throwing must not stop the others; a page that fails to
        // apply an update is a page problem, not a connection problem.
        try {
          handler(payload);
        } catch {
          // Deliberately swallowed - see above.
        }
      }
    });
  }

  /**
   * Tries again after a first connect that failed.
   *
   * withAutomaticReconnect only covers a connection that was established
   * once, so without this a panel opened while the API was down would never
   * reconnect on its own.
   */
  private scheduleRetry(): void {
    setTimeout(() => {
      if (this.connection?.state === HubConnectionState.Disconnected) {
        void this.connection.start().then(
          // The pages loaded their data while there was no connection, so
          // this late first connect counts as coming back.
          () => this.connectedAgain(),
          () => this.scheduleRetry(),
        );
      }
    }, 5_000);
  }
}
