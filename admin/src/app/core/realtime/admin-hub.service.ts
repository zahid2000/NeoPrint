import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  IRetryPolicy,
  LogLevel,
  RetryContext,
} from '@microsoft/signalr';

import { AuthService } from '../auth/auth.service';

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
 * The access token lives fifteen minutes and the connection outlives it, so
 * `accessTokenFactory` is read again on every (re)connect rather than once at
 * startup.
 */
@Injectable({ providedIn: 'root' })
export class AdminHubService {
  private readonly auth = inject(AuthService);

  private connection: HubConnection | null = null;

  /** Handlers by event name, so several features can watch the same event. */
  private readonly handlers = new Map<string, Set<(payload: unknown) => void>>();

  private readonly state = signal<ConnectionState>('disconnected');

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

    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/admin', {
        // Read per connection attempt, not captured once: the access token
        // rotates every fifteen minutes.
        accessTokenFactory: () => this.auth.token ?? '',
      })
      .withAutomaticReconnect(new ForeverRetryPolicy())
      .configureLogging(LogLevel.Warning)
      .build();

    connection.onreconnecting(() => this.state.set('reconnecting'));
    connection.onreconnected(() => this.state.set('connected'));
    connection.onclose(() => this.state.set('disconnected'));

    this.connection = connection;

    // Handlers registered before the connection existed are attached now.
    for (const event of this.handlers.keys()) {
      this.attach(event);
    }

    try {
      await connection.start();
      this.state.set('connected');
    } catch {
      // A failed first connect is not an error worth showing: the panel works
      // without real-time, it just does not update on its own. The indicator
      // says so, and the retry below picks it up.
      this.state.set('disconnected');
      this.scheduleRetry();
    }
  }

  /**
   * Subscribes to an event and returns the unsubscribe function.
   *
   * The payload is typed by the caller, which knows the event's shape; the
   * service deliberately does not carry a union of every event in the system.
   */
  on<T>(event: string, handler: (payload: T) => void): () => void {
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

    this.state.set('disconnected');
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
          () => this.state.set('connected'),
          () => this.scheduleRetry(),
        );
      }
    }, 5_000);
  }
}
