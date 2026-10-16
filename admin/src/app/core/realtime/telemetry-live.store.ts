import { DestroyRef, Injectable, effect, inject, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { MonitoringApiService } from '../api/monitoring-api.service';
import { KioskTelemetry, PrinterReading, PrinterTelemetryUpdatedEvent } from '../api/step7.models';
import { AuthService } from '../auth/auth.service';
import { Problem, toProblem } from '../../shared/problem';
import { AdminHubService } from './admin-hub.service';
import { HubEvents } from './hub-events';
import { liveRefresh } from './live-refresh';

/** What a page watches: one kiosk's report, one kiosk's printers, or every printer. */
type Watched = `telemetry:${string}` | `printers:${string}` | 'all-printers';

/**
 * What kiosks and their printers last reported - once for the whole panel
 * (T-212, as the kiosk statuses are in {@link KioskLiveStore}, T-254).
 *
 * `KioskTelemetryUpdated` and `PrinterTelemetryUpdated` carry the whole
 * reading, so they are applied here and nowhere else: the kiosk's "Cari
 * vəziyyət" tab, the printer page and the printers page read the same
 * reading at the same moment, and none keeps a copy of its own.
 *
 * A page calls {@link watchTelemetry}, {@link watchPrinters} or
 * {@link watchAllPrinters} with its own DestroyRef: what it watches is let go
 * when the page goes. What is watched is fetched
 * then, again when the live connection comes back, and every thirty seconds
 * while there is none; the events keep it current in between.
 */
@Injectable({ providedIn: 'root' })
export class TelemetryLiveStore {
  private readonly api = inject(MonitoringApiService);
  private readonly auth = inject(AuthService);
  private readonly hub = inject(AdminHubService);

  private readonly telemetry = signal<ReadonlyMap<string, KioskTelemetry>>(new Map());
  private readonly printers = signal<ReadonlyMap<string, readonly PrinterReading[]>>(new Map());

  /** Whether every kiosk's printers have been fetched: until then the printers page has nothing to show. */
  private readonly everyKiosk = signal(false);

  private readonly watched = new Map<Watched, number>();
  private account = this.auth.admin()?.id ?? null;
  private generation = 0;

  constructor() {
    this.hub.on<KioskTelemetry>(HubEvents.kioskTelemetryUpdated, (reading) => this.learnTelemetry(reading));
    this.hub.on<PrinterTelemetryUpdatedEvent>(HubEvents.printerTelemetryUpdated, (event) =>
      this.learnPrinters(event.kioskId, event.printers, false),
    );

    // Events missed while the connection was away are fetched again.
    liveRefresh({ events: [], reload: () => this.reloadWatched() });

    effect(() => {
      const account = this.auth.admin()?.id ?? null;

      if (account !== this.account) {
        this.account = account;
        untracked(() => this.forget());
      }
    });
  }

  /** A kiosk's last report, or undefined until it has been fetched. */
  telemetryOf(kioskId: string): KioskTelemetry | undefined {
    return this.telemetry().get(kioskId);
  }

  /** A kiosk's printers, the selected one first, or undefined until they have been fetched. */
  printersOf(kioskId: string): readonly PrinterReading[] | undefined {
    return this.printers().get(kioskId);
  }

  /** Every printer of every kiosk the account sees, or null until they have been fetched. */
  allPrinters(): readonly PrinterReading[] | null {
    return this.everyKiosk() ? [...this.printers().values()].flat() : null;
  }

  watchTelemetry(kioskId: string, page: DestroyRef): Promise<Problem | null> {
    this.watch(`telemetry:${kioskId}`, page);

    return this.loadTelemetry(kioskId);
  }

  watchPrinters(kioskId: string, page: DestroyRef): Promise<Problem | null> {
    this.watch(`printers:${kioskId}`, page);

    return this.loadPrinters(kioskId);
  }

  watchAllPrinters(page: DestroyRef): Promise<Problem | null> {
    this.watch('all-printers', page);

    return this.loadAllPrinters();
  }

  /** Fetches a kiosk's report; the answer, or why there is none. */
  async loadTelemetry(kioskId: string): Promise<Problem | null> {
    const generation = this.generation;

    try {
      const reading = await firstValueFrom(this.api.kioskTelemetry(kioskId));

      if (generation === this.generation) {
        this.learnTelemetry(reading);
      }

      return null;
    } catch (error) {
      return toProblem(error);
    }
  }

  async loadPrinters(kioskId: string): Promise<Problem | null> {
    const generation = this.generation;

    try {
      const readings = await firstValueFrom(this.api.kioskPrinterReadings(kioskId));

      if (generation === this.generation) {
        this.learnPrinters(kioskId, readings, true);
      }

      return null;
    } catch (error) {
      return toProblem(error);
    }
  }

  async loadAllPrinters(): Promise<Problem | null> {
    const generation = this.generation;

    try {
      const readings = await firstValueFrom(this.api.allPrinterReadings());

      if (generation !== this.generation) {
        return null;
      }

      const byKiosk = new Map<string, PrinterReading[]>();
      readings.forEach((reading) => byKiosk.set(reading.kioskId, [...(byKiosk.get(reading.kioskId) ?? []), reading]));

      // The answer is the whole truth for the scope: a kiosk it does not name has no printer to show.
      this.printers.set(byKiosk);
      this.everyKiosk.set(true);

      return null;
    } catch (error) {
      return toProblem(error);
    }
  }

  private learnTelemetry(reading: KioskTelemetry): void {
    this.telemetry.update((known) => new Map(known).set(reading.kioskId, reading));
  }

  /**
   * A fetch names all of a kiosk's printers; an event only those the kiosk
   * reported just now - the selected one every minute, the others every ten.
   */
  private learnPrinters(kioskId: string, readings: readonly PrinterReading[], whole: boolean): void {
    this.printers.update((known) => {
      const before = whole ? [] : (known.get(kioskId) ?? []);
      const merged = [...before];

      for (const reading of readings) {
        const at = merged.findIndex((existing) => existing.sample.printer.printerName === reading.sample.printer.printerName);

        if (at < 0) {
          merged.push(reading);
        } else {
          merged[at] = reading;
        }
      }

      // The selected printer first, then by name - as the server orders them.
      merged.sort(
        (a, b) =>
          Number(b.sample.selected) - Number(a.sample.selected) ||
          a.sample.printer.printerName.localeCompare(b.sample.printer.printerName),
      );

      return new Map(known).set(kioskId, merged);
    });
  }

  private watch(key: Watched, page: DestroyRef): void {
    this.watched.set(key, (this.watched.get(key) ?? 0) + 1);

    page.onDestroy(() => {
      const left = (this.watched.get(key) ?? 1) - 1;

      if (left > 0) {
        this.watched.set(key, left);
      } else {
        this.watched.delete(key);
      }
    });
  }

  private async reloadWatched(): Promise<void> {
    await Promise.all(
      [...this.watched.keys()].map((key) =>
        key === 'all-printers'
          ? this.loadAllPrinters()
          : key.startsWith('telemetry:')
            ? this.loadTelemetry(key.slice('telemetry:'.length))
            : this.loadPrinters(key.slice('printers:'.length)),
      ),
    );
  }

  private forget(): void {
    this.generation++;
    this.telemetry.set(new Map());
    this.printers.set(new Map());
    this.everyKiosk.set(false);

    if (this.account && this.watched.size > 0) {
      void this.reloadWatched();
    }
  }
}
