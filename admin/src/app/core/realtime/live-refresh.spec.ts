import { DOCUMENT } from '@angular/common';
import { EnvironmentInjector, createEnvironmentInjector, runInInjectionContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { AdminHubService, ConnectionState } from './admin-hub.service';
import { HubEvent } from './hub-events';
import { liveDebounceMs, livePollMs, liveRefresh } from './live-refresh';

/** The hub as a page sees it: events, the connection's state, its coming back. */
class FakeHub {
  private readonly handlers = new Map<string, Set<(payload: unknown) => void>>();
  private readonly stateListeners = new Set<(state: ConnectionState) => void>();
  private readonly reconnectListeners = new Set<() => void>();

  private current: ConnectionState = 'connected';

  connectionState = (): ConnectionState => this.current;

  on<T>(event: string, handler: (payload: T) => void): () => void {
    const handlers = this.handlers.get(event) ?? new Set();
    this.handlers.set(event, handlers);
    handlers.add(handler as (payload: unknown) => void);

    return () => handlers.delete(handler as (payload: unknown) => void);
  }

  onState(listener: (state: ConnectionState) => void): () => void {
    this.stateListeners.add(listener);

    return () => this.stateListeners.delete(listener);
  }

  onReconnect(listener: () => void): () => void {
    this.reconnectListeners.add(listener);

    return () => this.reconnectListeners.delete(listener);
  }

  emit(event: string, payload: unknown = {}): void {
    this.handlers.get(event)?.forEach((handler) => handler(payload));
  }

  become(state: ConnectionState): void {
    this.current = state;
    this.stateListeners.forEach((listener) => listener(state));
  }

  comeBack(): void {
    this.become('connected');
    this.reconnectListeners.forEach((listener) => listener());
  }

  get listening(): number {
    return (
      [...this.handlers.values()].reduce((count, handlers) => count + handlers.size, 0) +
      this.stateListeners.size +
      this.reconnectListeners.size
    );
  }
}

describe('liveRefresh', () => {
  let hub: FakeHub;
  let page: EnvironmentInjector | null = null;
  let reload: ReturnType<typeof vi.fn<() => void>>;

  /** The page is closed: what a component's destruction does. */
  function close(): void {
    page?.destroy();
    page = null;
  }

  /** Makes a "page" that keeps itself current, the way a component's constructor does. */
  function open(options: { events: HubEvent[]; accepts?: (event: HubEvent, payload: unknown) => boolean }): void {
    const injector = createEnvironmentInjector([], TestBed.inject(EnvironmentInjector));
    page = injector;
    runInInjectionContext(injector, () => liveRefresh({ ...options, reload }));
  }

  beforeEach(() => {
    vi.useFakeTimers();

    hub = new FakeHub();
    reload = vi.fn<() => void>();

    TestBed.configureTestingModule({ providers: [{ provide: AdminHubService, useValue: hub }] });
  });

  afterEach(() => {
    close();
    vi.useRealTimers();
  });

  it('fetches again shortly after an event the page listens to', () => {
    open({ events: ['KioskStatusChanged'] });

    hub.emit('KioskStatusChanged', { kioskId: 'k-1', status: 'Offline' });

    expect(reload).not.toHaveBeenCalled();

    vi.advanceTimersByTime(liveDebounceMs);

    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('answers a burst of events with one fetch', () => {
    open({ events: ['KioskStatusChanged', 'DeviceActivated'] });

    hub.emit('KioskStatusChanged');
    vi.advanceTimersByTime(liveDebounceMs / 2);
    hub.emit('DeviceActivated');
    vi.advanceTimersByTime(liveDebounceMs / 2);
    hub.emit('KioskStatusChanged');
    vi.advanceTimersByTime(liveDebounceMs);

    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('ignores an event that is about something else', () => {
    open({
      events: ['KioskStatusChanged'],
      accepts: (_event, payload) => (payload as { kioskId: string }).kioskId === 'mine',
    });

    hub.emit('KioskStatusChanged', { kioskId: 'another' });
    hub.emit('QueueUpdated', { kioskId: 'mine' });
    vi.advanceTimersByTime(liveDebounceMs * 2);

    expect(reload).not.toHaveBeenCalled();

    hub.emit('KioskStatusChanged', { kioskId: 'mine' });
    vi.advanceTimersByTime(liveDebounceMs);

    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('fetches at once when the connection comes back: events sent meanwhile were not heard', () => {
    open({ events: ['KioskStatusChanged'] });

    hub.become('reconnecting');
    hub.comeBack();

    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('asks every thirty seconds while there is no live connection, and stops when there is one', () => {
    open({ events: ['KioskStatusChanged'] });

    vi.advanceTimersByTime(livePollMs * 2);
    expect(reload, 'connected: events are enough').not.toHaveBeenCalled();

    hub.become('reconnecting');
    vi.advanceTimersByTime(livePollMs * 3);
    expect(reload).toHaveBeenCalledTimes(3);

    // Still away, in another state: one timer, not two.
    hub.become('disconnected');
    vi.advanceTimersByTime(livePollMs);
    expect(reload).toHaveBeenCalledTimes(4);

    hub.comeBack();
    expect(reload, 'the catch-up fetch').toHaveBeenCalledTimes(5);

    vi.advanceTimersByTime(livePollMs * 3);
    expect(reload, 'no more polling').toHaveBeenCalledTimes(5);
  });

  it('polls from the start on a page opened while the panel is cut off', () => {
    hub.become('disconnected');
    open({ events: [] });

    vi.advanceTimersByTime(livePollMs);

    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('fetches when the tab is looked at again after a while, not after a glance away', () => {
    open({ events: [] });

    const view = TestBed.inject(DOCUMENT);
    let visibility: DocumentVisibilityState = 'visible';
    vi.spyOn(view, 'visibilityState', 'get').mockImplementation(() => visibility);

    const turn = (to: DocumentVisibilityState): void => {
      visibility = to;
      view.dispatchEvent(new Event('visibilitychange'));
    };

    turn('hidden');
    vi.advanceTimersByTime(5_000);
    turn('visible');
    expect(reload).not.toHaveBeenCalled();

    turn('hidden');
    vi.advanceTimersByTime(livePollMs);
    turn('visible');
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('leaves nothing behind when the page goes', () => {
    open({ events: ['KioskStatusChanged'] });
    hub.become('disconnected');
    hub.emit('KioskStatusChanged');

    close();

    expect(hub.listening).toBe(0);

    vi.advanceTimersByTime(livePollMs * 2);
    hub.emit('KioskStatusChanged');
    vi.advanceTimersByTime(liveDebounceMs);

    expect(reload).not.toHaveBeenCalled();
  });
});
