import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RetryContext } from '@microsoft/signalr';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { AuthService } from '../auth/auth.service';
import { AdminHubService, ForeverRetryPolicy, HUB_CONNECTION_BUILDER } from './admin-hub.service';

/**
 * A stand-in for a SignalR connection.
 *
 * The real one opens a WebSocket, which jsdom has no answer for. What matters
 * here is what the service does with the connection, not that SignalR can
 * negotiate a transport.
 */
class FakeConnection {
  readonly handlers = new Map<string, (payload: unknown) => void>();

  state = 'Disconnected';

  startCalls = 0;
  stopCalls = 0;

  private startBehaviour: () => Promise<void> = () => Promise.resolve();

  onreconnecting = vi.fn();
  onreconnected = vi.fn();
  onclose = vi.fn();

  failNextStart(): void {
    this.startBehaviour = () => Promise.reject(new Error('no route to host'));
  }

  succeedFromNowOn(): void {
    this.startBehaviour = () => Promise.resolve();
  }

  on(event: string, handler: (payload: unknown) => void): void {
    this.handlers.set(event, handler);
  }

  async start(): Promise<void> {
    this.startCalls++;
    await this.startBehaviour();
    this.state = 'Connected';
  }

  async stop(): Promise<void> {
    this.stopCalls++;
    this.state = 'Disconnected';
  }

  /** Delivers an event as the server would. */
  emit(event: string, payload: unknown): void {
    this.handlers.get(event)?.(payload);
  }
}

let connection: FakeConnection;

/** What the service asked the connection to be opened with. */
let urlOptions: { accessTokenFactory?: () => string | Promise<string> } | undefined;

/** Builds the fake connection the test controls. */
class FakeBuilder {
  withUrl(_url: string, options?: { accessTokenFactory?: () => string | Promise<string> }) {
    urlOptions = options;
    return this;
  }
  withAutomaticReconnect() {
    return this;
  }
  configureLogging() {
    return this;
  }
  build() {
    return connection;
  }
}

describe('AdminHubService', () => {
  let service: AdminHubService;

  beforeEach(() => {
    connection = new FakeConnection();

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: HUB_CONNECTION_BUILDER, useValue: () => new FakeBuilder() },
      ],
    });

    service = TestBed.inject(AdminHubService);
  });

  it('reports the connection as live once it opens', async () => {
    expect(service.connectionState()).toBe('disconnected');

    await service.start();

    expect(connection.startCalls).toBe(1);
    expect(service.connectionState()).toBe('connected');
  });

  it('opens one connection however many times it is started', async () => {
    await service.start();
    await service.start();

    // The hub puts each connection into the groups the account may see, so a
    // second connection would double every event.
    expect(connection.startCalls).toBe(1);
  });

  it('delivers an event to every handler registered for it', async () => {
    const first = vi.fn();
    const second = vi.fn();

    service.on('PermissionsChanged', first);
    service.on('PermissionsChanged', second);

    await service.start();

    connection.emit('PermissionsChanged', { roleId: 'r-1' });

    expect(first).toHaveBeenCalledWith({ roleId: 'r-1' });
    expect(second).toHaveBeenCalledWith({ roleId: 'r-1' });

    // One SignalR callback per event name, not one per handler.
    expect(connection.handlers.size).toBe(1);
  });

  it('stops delivering to a handler that unsubscribed', async () => {
    const handler = vi.fn();
    const unsubscribe = service.on('DeviceRegistered', handler);

    await service.start();
    unsubscribe();

    connection.emit('DeviceRegistered', { deviceId: 'd-1' });

    expect(handler).not.toHaveBeenCalled();
  });

  it('one handler throwing does not stop the others', async () => {
    const broken = vi.fn(() => {
      throw new Error('a page failed to apply the update');
    });
    const working = vi.fn();

    service.on('DeviceActivated', broken);
    service.on('DeviceActivated', working);

    await service.start();

    connection.emit('DeviceActivated', { deviceId: 'd-1' });

    expect(working).toHaveBeenCalled();
  });

  it('records when the last event arrived', async () => {
    service.on('FeatureFlagsChanged', () => undefined);
    await service.start();

    expect(service.lastEventAt()).toBeNull();

    connection.emit('FeatureFlagsChanged', {});

    expect(service.lastEventAt()).toBeInstanceOf(Date);
  });

  it('a first connect that fails leaves the panel usable and offline', async () => {
    connection.failNextStart();

    await service.start();

    // Not a thrown error: the panel works without real-time, it just does not
    // update on its own, and the indicator is what says so.
    expect(service.connectionState()).toBe('disconnected');
  });

  it('attaches handlers registered before the connection existed', async () => {
    const handler = vi.fn();

    service.on('DeviceRevoked', handler);
    await service.start();

    connection.emit('DeviceRevoked', { deviceId: 'd-9' });

    expect(handler).toHaveBeenCalled();
  });

  it('says so when the connection comes back, but not when it first opens', async () => {
    const reconnected = vi.fn();
    const states: string[] = [];

    service.onReconnect(reconnected);
    service.onState((state) => states.push(state));

    await service.start();

    // A page has just loaded its data: nothing was missed.
    expect(reconnected).not.toHaveBeenCalled();

    // The API restarts: SignalR reports the drop, then the return.
    (connection.onreconnecting.mock.calls[0][0] as () => void)();
    expect(service.connectionState()).toBe('reconnecting');

    (connection.onreconnected.mock.calls[0][0] as () => void)();
    expect(service.connectionState()).toBe('connected');

    // Events sent in between were not heard: the pages fetch again.
    expect(reconnected).toHaveBeenCalledTimes(1);
    expect(states).toEqual(['connected', 'reconnecting', 'connected']);
  });

  it('a first connect that only succeeds later counts as coming back', async () => {
    vi.useFakeTimers();

    try {
      const reconnected = vi.fn();
      service.onReconnect(reconnected);

      connection.failNextStart();
      await service.start();
      expect(service.connectionState()).toBe('disconnected');

      // The pages loaded while there was no connection; the retry gets through.
      connection.succeedFromNowOn();
      await vi.advanceTimersByTimeAsync(5_000);

      expect(service.connectionState()).toBe('connected');
      expect(reconnected).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it('a listener that was removed is not called, and one that throws does not stop the rest', async () => {
    const removed = vi.fn();
    const broken = vi.fn(() => {
      throw new Error('a page failed to refresh');
    });
    const working = vi.fn();

    service.onReconnect(removed)();
    service.onReconnect(broken);
    service.onReconnect(working);

    await service.start();
    (connection.onreconnected.mock.calls[0][0] as () => void)();

    expect(removed).not.toHaveBeenCalled();
    expect(working).toHaveBeenCalledTimes(1);
  });

  it('restarting opens a new connection and tells the pages to catch up', async () => {
    const reconnected = vi.fn();
    service.onReconnect(reconnected);

    await service.start();

    const first = connection;
    connection = new FakeConnection();

    // The account's kiosks changed: the server decides the groups on connect.
    await service.restart();

    expect(first.stopCalls).toBe(1);
    expect(connection.startCalls).toBe(1);
    expect(service.connectionState()).toBe('connected');
    expect(reconnected).toHaveBeenCalledTimes(1);
  });

  it('keeps the handlers of the pages across a restart', async () => {
    const handler = vi.fn();
    service.on('KioskStatusChanged', handler);

    await service.start();
    connection = new FakeConnection();
    await service.restart();

    connection.emit('KioskStatusChanged', { kioskId: 'k-1', status: 'Online' });

    expect(handler).toHaveBeenCalledWith({ kioskId: 'k-1', status: 'Online' });
  });

  it('opens every connection with a token that is good now', async () => {
    const fresh = vi.spyOn(TestBed.inject(AuthService), 'freshToken').mockResolvedValue('a-new-token');

    await service.start();

    // SignalR calls the factory on the first connect and on every reconnect.
    expect(await urlOptions?.accessTokenFactory?.()).toBe('a-new-token');
    expect(await urlOptions?.accessTokenFactory?.()).toBe('a-new-token');
    expect(fresh).toHaveBeenCalledTimes(2);
  });

  it('stopping closes the connection and clears the state', async () => {
    await service.start();
    await service.stop();

    expect(connection.stopCalls).toBe(1);
    expect(service.connectionState()).toBe('disconnected');
  });
});

describe('ForeverRetryPolicy', () => {
  const policy = new ForeverRetryPolicy();

  const delayAfter = (attempts: number) =>
    policy.nextRetryDelayInMilliseconds({
      previousRetryCount: attempts,
      elapsedMilliseconds: 0,
      retryReason: new Error('dropped'),
    } as RetryContext);

  it('retries immediately the first time', () => {
    expect(delayAfter(0)).toBe(0);
  });

  it('backs off, then keeps trying at a fixed interval', () => {
    expect(delayAfter(1)).toBeGreaterThan(0);
    expect(delayAfter(4)).toBe(30_000);

    // Never null: a panel left open all day has to come back on its own after
    // an API restart or a laptop waking from sleep. SignalR's default policy
    // gives up after about half a minute.
    expect(delayAfter(50)).toBe(30_000);
    expect(delayAfter(5_000)).toBe(30_000);
  });
});
