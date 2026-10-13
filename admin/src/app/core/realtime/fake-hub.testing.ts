import { ConnectionState } from './admin-hub.service';

/**
 * The hub as a page sees it, for specs: the test plays the server - it sends
 * events with {@link emit}, cuts the connection with {@link drop} and brings
 * it back with {@link comeBack}. Provided in place of `AdminHubService`.
 */
export class FakeHub {
  private readonly handlers = new Map<string, Set<(payload: unknown) => void>>();
  private readonly stateListeners = new Set<(state: ConnectionState) => void>();
  private readonly reconnectListeners = new Set<() => void>();

  private current: ConnectionState = 'connected';

  connectionState = (): ConnectionState => this.current;
  lastEventAt = (): Date | null => null;

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

  /** How many handlers wait for the event - zero for one nobody listens to. */
  listeners(event: string): number {
    return this.handlers.get(event)?.size ?? 0;
  }

  /** The server sends an event. */
  emit(event: string, payload?: unknown): void {
    this.handlers.get(event)?.forEach((handler) => handler(payload));
  }

  /** The connection is lost; the panel is trying to get it back. */
  drop(): void {
    this.current = 'reconnecting';
    this.stateListeners.forEach((listener) => listener(this.current));
  }

  /** The connection is back. */
  comeBack(): void {
    this.current = 'connected';
    this.stateListeners.forEach((listener) => listener(this.current));
    this.reconnectListeners.forEach((listener) => listener());
  }
}
