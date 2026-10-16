import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom, map } from 'rxjs';

import { AdminApiService } from '../../core/api/admin-api.service';
import { MonitoringApiService } from '../../core/api/monitoring-api.service';
import { PrinterHistoryEvent, PrinterHistoryPoint, PrinterReading } from '../../core/api/step7.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { commandResults } from '../../core/realtime/command-results';
import { KioskLiveStore } from '../../core/realtime/kiosk-live.store';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { TelemetryLiveStore } from '../../core/realtime/telemetry-live.store';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { BytesPipe, DateTimePipe, formatCount, formatDateTime } from '../../shared/format.pipes';
import { LineChartComponent, LineSeries } from '../../shared/line-chart.component';
import { NotifyService } from '../../shared/notify.service';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { printerReasons, printerStatus } from './printer-status';

type Range = '7d' | '30d';

interface Row {
  readonly key: string;
  readonly value: string | null;
}

/** A spooler job this long in the queue is stuck (plan section 7.10). */
const stuckAfterMs = 5 * 60_000;

/**
 * Printer detail (plan sections 7.10, 8A.3): who the printer is, how it is
 * now, its supplies and trays, its counters over time, what it can do, what
 * waits in the system spooler, and what has happened to it - with the test
 * page, "Default et" and "Yenilə". The reading is the panel's one copy
 * ({@link TelemetryLiveStore}), live.
 */
@Component({
  selector: 'app-printer-detail',
  imports: [
    FormsModule,
    RouterLink,
    MatButtonModule,
    MatButtonToggleModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTableModule,
    TranslatePipe,
    BytesPipe,
    DateTimePipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    LineChartComponent,
    PageHeaderComponent,
    StatusChipComponent,
  ],
  template: `
    <div class="neo-page">
      <a mat-button [routerLink]="['/kiosks', id()]" class="neo-back">
        <mat-icon>arrow_back</mat-icon>
        {{ 'monitoring.printer.back' | translate: { kiosk: kioskCode() } }}
      </a>

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="monitoring.printer.loadFailedEffect" (retry)="load()" />
      } @else if (readings() === undefined) {
        <div class="neo-skeleton neo-skeleton--block"></div>
      } @else if (reading(); as current) {
        <app-page-header [title]="name()" [description]="model(current)">
          <app-status-chip
            [status]="status()!.key"
            [toneOverride]="status()!.tone"
            [iconOverride]="status()!.icon"
            [text]="'monitoring.printerState.' + status()!.key | translate"
          />
          @if (current.sample.selected) {
            <app-status-chip status="Selected" toneOverride="success" [text]="'printers.selected' | translate" />
          }
        </app-page-header>

        <div class="printer__actions">
          @if (current.sample.selected && canTestPrint()) {
            <button mat-flat-button type="button" [disabled]="busy() !== null" (click)="testPrint()">
              @if (busy() === 'test') {
                <mat-spinner diameter="18" class="neo-button-spinner" />
              } @else {
                <mat-icon>print</mat-icon>
              }
              {{ 'printers.testPrint' | translate }}
            </button>
          }
          @if (!current.sample.selected && canSelect()) {
            <button mat-flat-button type="button" [disabled]="busy() !== null" (click)="select()">
              @if (busy() === 'select') {
                <mat-spinner diameter="18" class="neo-button-spinner" />
              }
              {{ 'printers.select' | translate }}
            </button>
          }
          @if (canRefresh()) {
            <button mat-stroked-button type="button" [disabled]="busy() !== null" (click)="refresh()">
              @if (busy() === 'refresh') {
                <mat-spinner diameter="18" class="neo-button-spinner" />
              } @else {
                <mat-icon>refresh</mat-icon>
              }
              {{ 'printers.refresh' | translate }}
            </button>
          }
        </div>

        @if (current.stale) {
          <div class="neo-notice" role="status">
            <mat-icon aria-hidden="true">timer_off</mat-icon>
            <span>{{ 'monitoring.printer.stale' | translate: { time: (current.sample.timestamp | dateTime) } }}</span>
          </div>
        } @else {
          <p class="neo-caption">
            {{ 'monitoring.status.reportedAt' | translate: { time: (current.sample.timestamp | dateTime) } }}
          </p>
        }

        <div class="printer__grid">
          <section class="neo-card printer__card">
            <h3>{{ 'monitoring.printer.identity' | translate }}</h3>
            <dl>
              @for (row of identity(); track row.key) {
                <div class="neo-metric">
                  <dt>{{ 'monitoring.printer.' + row.key | translate }}</dt>
                  <dd>
                    @if (row.value === null) {
                      <span class="neo-caption">{{ 'monitoring.printerNotReported' | translate }}</span>
                    } @else {
                      {{ row.value }}
                    }
                  </dd>
                </div>
              }
            </dl>
          </section>

          <section class="neo-card printer__card">
            <h3>{{ 'monitoring.printer.state' | translate }}</h3>
            <dl>
              <div class="neo-metric">
                <dt>{{ 'monitoring.printer.stateNow' | translate }}</dt>
                <dd>
                  @if (current.sample.state) {
                    {{ 'monitoring.printerStateRaw.' + current.sample.state | translate }}
                  } @else {
                    <span class="neo-caption">{{ 'monitoring.printerNotReported' | translate }}</span>
                  }
                </dd>
              </div>
              <div class="neo-metric">
                <dt>{{ 'monitoring.printer.reachable' | translate }}</dt>
                <dd>
                  @if (current.sample.online === null || current.sample.online === undefined) {
                    <span class="neo-caption">{{ 'monitoring.printerNotReported' | translate }}</span>
                  } @else {
                    {{ (current.sample.online ? 'printers.online' : 'printers.offline') | translate }}
                  }
                </dd>
              </div>
              <div class="neo-metric" [class.neo-metric--warning]="reasons().length > 0">
                <dt>{{ 'monitoring.printer.reasons' | translate }}</dt>
                <dd>
                  @for (reason of reasons(); track reason) {
                    <code>{{ reason }}</code>
                  } @empty {
                    {{ 'monitoring.value.none' | translate }}
                  }
                </dd>
              </div>
              <div class="neo-metric" [class.neo-metric--warning]="!!current.sample.lastError">
                <dt>{{ 'monitoring.printer.lastError' | translate }}</dt>
                <dd>{{ current.sample.lastError ?? ('monitoring.value.none' | translate) }}</dd>
              </div>
            </dl>
          </section>

          <section class="neo-card printer__card">
            <h3>{{ 'monitoring.printer.supplies' | translate }}</h3>
            @for (supply of current.sample.supplies; track supply.name) {
              <div class="printer__level">
                <span class="printer__level-head">
                  <span>{{ supply.name }}</span>
                  <strong>
                    @if (supply.levelPercent === null || supply.levelPercent === undefined) {
                      <span class="neo-caption">{{ 'monitoring.printerNotReported' | translate }}</span>
                    } @else {
                      {{ supply.levelPercent }}%
                    }
                  </strong>
                </span>
                @if (supply.levelPercent !== null && supply.levelPercent !== undefined) {
                  <div class="printer__bar" role="progressbar" [attr.aria-valuenow]="supply.levelPercent" aria-valuemin="0" aria-valuemax="100">
                    <span class="printer__fill" [class.printer__fill--low]="supply.levelPercent < lowPercent" [style.width.%]="supply.levelPercent"></span>
                  </div>
                }
              </div>
            } @empty {
              <p class="neo-caption">{{ 'monitoring.printerNotReported' | translate }}</p>
            }

            <h3 class="printer__sub">{{ 'monitoring.printer.trays' | translate }}</h3>
            @for (tray of current.sample.trays; track tray.name) {
              <div class="neo-metric">
                <dt>{{ tray.name }}@if (tray.media) { · {{ tray.media }} }</dt>
                <dd>
                  @if (tray.levelPercent === null || tray.levelPercent === undefined) {
                    <span class="neo-caption">{{ 'monitoring.printerNotReported' | translate }}</span>
                  } @else {
                    {{ tray.levelPercent }}%
                  }
                </dd>
              </div>
            } @empty {
              <p class="neo-caption">{{ 'monitoring.printerNotReported' | translate }}</p>
            }
          </section>

          <section class="neo-card printer__card">
            <h3>{{ 'monitoring.printer.counters' | translate }}</h3>
            <dl>
              @for (row of counters(); track row.key) {
                <div class="neo-metric">
                  <dt>{{ 'monitoring.printer.' + row.key | translate }}</dt>
                  <dd>
                    @if (row.value === null) {
                      <span class="neo-caption">{{ 'monitoring.printerNotReported' | translate }}</span>
                    } @else {
                      {{ row.value }}
                    }
                  </dd>
                </div>
              }
            </dl>

            <h3 class="printer__sub">{{ 'monitoring.printer.capabilities' | translate }}</h3>
            <dl>
              @for (row of capabilities(); track row.key) {
                <div class="neo-metric">
                  <dt>{{ 'monitoring.printer.' + row.key | translate }}</dt>
                  <dd>
                    @if (row.value === null) {
                      <span class="neo-caption">{{ 'monitoring.printerNotReported' | translate }}</span>
                    } @else {
                      {{ row.value }}
                    }
                  </dd>
                </div>
              }
            </dl>
          </section>
        </div>

        <section class="neo-card printer__card">
          <div class="printer__head">
            <h3>{{ 'monitoring.printer.history' | translate }}</h3>
            <mat-button-toggle-group
              [attr.aria-label]="'monitoring.charts.range' | translate"
              [ngModel]="range()"
              (ngModelChange)="setRange($event)"
            >
              <mat-button-toggle value="7d">{{ 'monitoring.charts.week' | translate }}</mat-button-toggle>
              <mat-button-toggle value="30d">{{ 'monitoring.charts.month' | translate }}</mat-button-toggle>
            </mat-button-toggle-group>
          </div>
          @if (historyProblem(); as problem) {
            <app-error-banner [problem]="problem" effect="monitoring.charts.loadFailedEffect" (retry)="loadHistory()" />
          } @else if (history() === null) {
            <div class="neo-skeleton neo-skeleton--block"></div>
          } @else if (history()!.length === 0) {
            <p class="neo-caption">{{ 'monitoring.charts.empty' | translate }}</p>
          } @else {
            <div class="printer__charts">
              <figure class="printer__chart">
                <figcaption>{{ 'monitoring.printer.lifetimeChart' | translate }}</figcaption>
                <app-line-chart
                  [labels]="labels()"
                  [series]="pagesSeries()"
                  [fromZero]="false"
                  [format]="count"
                  [label]="'monitoring.printer.lifetimeChart' | translate"
                />
                <p class="neo-caption">{{ pagesSummary() }}</p>
              </figure>
              <figure class="printer__chart">
                <figcaption>{{ 'monitoring.printer.levelsChart' | translate }}</figcaption>
                @if (levelSeries().length === 0) {
                  <p class="neo-caption">{{ 'monitoring.charts.empty' | translate }}</p>
                } @else {
                  <app-line-chart
                    [labels]="labels()"
                    [series]="levelSeries()"
                    [format]="percent"
                    [label]="'monitoring.printer.levelsChart' | translate"
                  />
                  <p class="neo-caption">{{ levelsSummary() }}</p>
                }
              </figure>
            </div>
          }
        </section>

        <section class="neo-card printer__card">
          <h3>{{ 'monitoring.printer.spooler' | translate }}</h3>
          @if (current.sample.spoolerJobs.length === 0) {
            <p class="neo-caption">{{ 'monitoring.printer.spoolerEmpty' | translate }}</p>
          } @else {
            <div class="neo-table-wrapper">
              <table mat-table class="neo-table" [dataSource]="current.sample.spoolerJobs">
                <ng-container matColumnDef="id">
                  <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.printer.colJob' | translate }}</th>
                  <td mat-cell *matCellDef="let job"><code>{{ job.id }}</code></td>
                </ng-container>
                <ng-container matColumnDef="size">
                  <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.printer.colSize' | translate }}</th>
                  <td mat-cell *matCellDef="let job" class="neo-nowrap">{{ job.sizeBytes | bytes }}</td>
                </ng-container>
                <ng-container matColumnDef="state">
                  <th mat-header-cell *matHeaderCellDef>{{ 'printers.colStatus' | translate }}</th>
                  <td mat-cell *matCellDef="let job">
                    {{ job.state ?? '' }}
                    @if (stuck(job.id, job.submittedAt)) {
                      <app-status-chip status="Stuck" toneOverride="warning" [text]="'monitoring.printer.stuck' | translate" />
                    }
                  </td>
                </ng-container>
                <ng-container matColumnDef="since">
                  <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.printer.colSince' | translate }}</th>
                  <td mat-cell *matCellDef="let job" class="neo-nowrap">{{ since(job.id, job.submittedAt) | dateTime }}</td>
                </ng-container>
                <tr mat-header-row *matHeaderRowDef="spoolerColumns"></tr>
                <tr mat-row *matRowDef="let row; columns: spoolerColumns"></tr>
              </table>
            </div>
          }
        </section>

        <section class="neo-card printer__card">
          <h3>{{ 'monitoring.printer.events' | translate }}</h3>
          @if (events() === null) {
            <div class="neo-skeleton neo-skeleton--block"></div>
          } @else if (events()!.length === 0) {
            <p class="neo-caption">{{ 'monitoring.printer.eventsEmpty' | translate }}</p>
          } @else {
            <ol class="printer__timeline">
              @for (event of events(); track $index) {
                <li>
                  <span class="printer__when neo-nowrap">{{ event.at | dateTime }}</span>
                  <span>
                    <strong>{{ 'monitoring.printerEvent.' + event.kind | translate }}</strong>
                    @if (event.subject) {
                      · <code>{{ event.subject }}</code>
                    }
                    @if (change(event); as change) {
                      <span class="neo-caption printer__block">{{ change }}</span>
                    }
                  </span>
                </li>
              }
            </ol>
          }
        </section>
      } @else {
        <app-empty-state
          icon="print_disabled"
          [message]="'monitoring.printer.notFound' | translate"
          [hint]="'monitoring.printer.notFoundHint' | translate"
        >
          <a mat-stroked-button [routerLink]="['/kiosks', id()]">{{ 'monitoring.printer.toKiosk' | translate }}</a>
        </app-empty-state>
      }
    </div>
  `,
  styles: `
    h3 {
      margin: 0 0 var(--space-3);
    }

    .printer__sub {
      margin-block-start: var(--space-4);
    }

    .printer__actions,
    .printer__head {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-3);
      align-items: center;
    }

    .printer__head {
      justify-content: space-between;
      margin-block-end: var(--space-4);
    }

    .printer__head h3 {
      margin: 0;
    }

    .printer__grid,
    .printer__charts {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
      gap: var(--space-4);
    }

    .printer__card {
      padding: var(--space-4);
    }

    .printer__card dl {
      margin: 0;
    }

    .printer__level {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
      margin-block-end: var(--space-3);
    }

    .printer__level-head {
      display: flex;
      gap: var(--space-2);
      justify-content: space-between;
    }

    .printer__bar {
      height: var(--space-2);
      border-radius: var(--radius-sm);
      background: var(--neutral-100);
      overflow: hidden;
    }

    .printer__fill {
      display: block;
      height: 100%;
      background: var(--success-500);
    }

    .printer__fill--low {
      background: var(--warning-500);
    }

    .printer__chart {
      margin: 0;
    }

    .printer__chart figcaption {
      margin-block-end: var(--space-2);
      font-weight: var(--font-weight-semibold);
    }

    .printer__timeline {
      display: flex;
      flex-direction: column;
      gap: var(--space-3);
      margin: 0;
      padding: 0;
      list-style: none;
    }

    .printer__timeline li {
      display: grid;
      grid-template-columns: minmax(120px, auto) 1fr;
      gap: var(--space-3);
    }

    .printer__when {
      color: var(--neutral-600);
      font-variant-numeric: tabular-nums;
    }

    .printer__block {
      display: block;
    }

    code {
      overflow-wrap: anywhere;
    }
  `,
})
export class PrinterDetailComponent {
  private readonly admin = inject(AdminApiService);
  private readonly api = inject(MonitoringApiService);
  private readonly auth = inject(AuthService);
  private readonly store = inject(TelemetryLiveStore);
  private readonly kiosks = inject(KioskLiveStore);
  private readonly notify = inject(NotifyService);
  private readonly translate = inject(TranslateService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly results = commandResults();

  /** The route's `:id` (the kiosk) and `:name` (the printer), bound by the router. */
  readonly id = input.required<string>();
  readonly name = input.required<string>();

  /** A level under this is drawn in the warning colour (plan 2.15's toner seed rule). */
  readonly lowPercent = 15;

  readonly spoolerColumns = ['id', 'size', 'state', 'since'];

  readonly problem = signal<Problem | null>(null);
  readonly busy = signal<'test' | 'select' | 'refresh' | null>(null);
  readonly range = signal<Range>('7d');
  readonly history = signal<readonly PrinterHistoryPoint[] | null>(null);
  readonly historyProblem = signal<Problem | null>(null);
  readonly events = signal<readonly PrinterHistoryEvent[] | null>(null);

  readonly canTestPrint = computed(() => this.auth.can(Permissions.printersTestPrint));
  readonly canSelect = computed(() => this.auth.can(Permissions.printersSelect));
  readonly canRefresh = computed(() => this.auth.can(Permissions.printersRefresh));

  private readonly language = toSignal(this.translate.onLangChange.pipe(map((change) => change.lang)), {
    initialValue: this.translate.getCurrentLang(),
  });

  readonly readings = computed(() => this.store.printersOf(this.id()));
  readonly reading = computed<PrinterReading | undefined>(() =>
    this.readings()?.find((reading) => reading.sample.printer.printerName === this.name()),
  );
  readonly status = computed(() => {
    const reading = this.reading();

    return reading ? printerStatus(reading) : null;
  });
  readonly reasons = computed(() => {
    const reading = this.reading();

    return reading ? printerReasons(reading) : [];
  });

  readonly kioskCode = computed(() => this.kiosks.kiosks()?.find((kiosk) => kiosk.id === this.id())?.code ?? '');

  readonly identity = computed<Row[]>(() => {
    const sample = this.reading()?.sample;
    this.language();

    if (!sample) {
      return [];
    }

    const connection = sample.connection
      ? this.text(`monitoring.printerConnection.${sample.connection}`) + (sample.address ? ` · ${sample.address}` : '')
      : null;

    return [
      { key: 'manufacturer', value: sample.manufacturer ?? null },
      { key: 'model', value: sample.model ?? null },
      { key: 'serial', value: sample.serialNumber ?? null },
      { key: 'firmware', value: sample.firmware ?? null },
      { key: 'driver', value: sample.driverName ? [sample.driverName, sample.driverVersion].filter(Boolean).join(' ') : null },
      { key: 'connection', value: connection },
      { key: 'mac', value: sample.macAddress ?? null },
      { key: 'uri', value: sample.uri ?? null },
    ];
  });

  readonly counters = computed<Row[]>(() => {
    const sample = this.reading()?.sample;
    const language = this.language();

    if (!sample) {
      return [];
    }

    const pages = (value: number | null | undefined): string | null => (value == null ? null : formatCount(value, language));

    return [
      { key: 'lifetimePages', value: pages(sample.lifetimePages) },
      { key: 'colorPages', value: pages(sample.colorPages) },
      { key: 'monoPages', value: pages(sample.monoPages) },
    ];
  });

  readonly capabilities = computed<Row[]>(() => {
    const features = this.reading()?.sample.features;
    this.language();

    const yesNo = (value: boolean | null | undefined): string | null =>
      value == null ? null : this.text(value ? 'monitoring.value.yes' : 'monitoring.value.no');
    const list = (values: readonly string[] | undefined): string | null => (values && values.length > 0 ? values.join(', ') : null);

    return [
      { key: 'color', value: yesNo(features?.color) },
      { key: 'duplex', value: yesNo(features?.duplex) },
      { key: 'paperSizes', value: list(features?.paperSizes) },
      { key: 'trayNames', value: list(features?.trays) },
      { key: 'resolution', value: features?.resolutionDpi ? `${features.resolutionDpi} DPI` : null },
      { key: 'formats', value: list(features?.nativeFormats) },
    ];
  });

  readonly labels = computed(() => (this.history() ?? []).map((point) => formatDateTime(point.at)));

  readonly pagesSeries = computed<LineSeries[]>(() => [
    {
      label: this.text('monitoring.printer.lifetimePages'),
      values: (this.history() ?? []).map((point) => point.lifetimePages ?? null),
    },
  ]);

  readonly levelSeries = computed<LineSeries[]>(() => {
    const points = this.history() ?? [];
    const names = [...new Set(points.flatMap((point) => Object.keys(point.supplyLevels)))];

    return names
      .map((name) => ({ label: name, values: points.map((point) => point.supplyLevels[name] ?? null) }))
      .filter((line) => line.values.some((value) => value !== null));
  });

  readonly pagesSummary = computed(() => {
    const values = this.pagesSeries()[0].values.filter((value): value is number => value !== null);

    return values.length < 1
      ? ''
      : this.text('monitoring.printer.pagesSummary', {
          pages: formatCount(values[values.length - 1] - values[0], this.language()),
        });
  });

  readonly levelsSummary = computed(() =>
    this.levelSeries()
      .map((line) => {
        const known = line.values.filter((value): value is number => value !== null);

        return `${line.label}: ${known[known.length - 1]}%`;
      })
      .join(' · '),
  );

  readonly count = (value: number): string => formatCount(value, this.language());
  readonly percent = (value: number): string => `${Math.round(value)}%`;

  constructor() {
    this.kiosks.watch();

    // The route's printer may change while the page stays: everything is asked for again.
    effect(() => {
      const kioskId = this.id();
      this.name();

      untracked(() => {
        void this.load(kioskId);
        void this.loadHistory();
        void this.loadEvents();
      });
    });

    // A new reading of this printer can mean a new event in its history.
    liveRefresh({ events: [], reload: () => Promise.all([this.loadHistory(true), this.loadEvents()]) });
  }

  async load(kioskId = this.id()): Promise<void> {
    this.problem.set(null);
    this.problem.set(await this.store.watchPrinters(kioskId, this.destroyRef));
  }

  setRange(range: Range): void {
    this.range.set(range);
    this.history.set(null);
    void this.loadHistory();
  }

  async loadHistory(quiet = false): Promise<void> {
    const to = new Date();
    const from = new Date(to.getTime() - (this.range() === '7d' ? 7 : 30) * 86_400_000);

    if (!quiet) {
      this.historyProblem.set(null);
    }

    try {
      this.history.set(await firstValueFrom(this.api.printerHistory(this.id(), this.name(), from, to)));
    } catch (error) {
      if (!quiet) {
        this.historyProblem.set(toProblem(error));
      }
    }
  }

  async loadEvents(): Promise<void> {
    try {
      this.events.set(await firstValueFrom(this.api.printerEvents(this.id(), this.name())));
    } catch {
      // The history is secondary: the rest of the page stands without it.
      this.events.set([]);
    }
  }

  model(reading: PrinterReading): string {
    return [reading.sample.manufacturer, reading.sample.model].filter(Boolean).join(' ');
  }

  /** When a spooler job was first seen: the spooler's time if it gave one, else the first reading with it. */
  since(jobId: string, submittedAt?: string | null): string | null {
    return submittedAt ?? this.reading()?.sample.jobsSeenSince?.[jobId] ?? null;
  }

  stuck(jobId: string, submittedAt?: string | null): boolean {
    const since = this.since(jobId, submittedAt);
    const at = this.reading()?.sample.timestamp;

    return !!since && !!at && Date.parse(at) - Date.parse(since) >= stuckAfterMs;
  }

  /** "Black Cartridge: 5% → 100%", "idle → stopped". */
  change(event: PrinterHistoryEvent): string | null {
    if (event.fromLevel != null && event.toLevel != null) {
      return `${event.fromLevel}% → ${event.toLevel}%`;
    }

    return event.from || event.to ? `${event.from ?? '—'} → ${event.to ?? '—'}` : null;
  }

  async testPrint(): Promise<void> {
    await this.run('test', async () => {
      const command = await firstValueFrom(this.admin.testPrint(this.id()));
      this.notify.success('printers.sent');
      this.results.sent(command);
    });
  }

  async select(): Promise<void> {
    await this.run('select', async () => {
      await firstValueFrom(this.admin.selectKioskPrinter(this.id(), this.name()));
      this.notify.success('printers.saved');
      await this.store.loadPrinters(this.id());
    });
  }

  async refresh(): Promise<void> {
    await this.run('refresh', async () => {
      const command = await firstValueFrom(this.admin.refreshKioskPrinters(this.id()));
      this.notify.success('printers.sent');
      this.results.sent(command);
    });
  }

  private async run(part: 'test' | 'select' | 'refresh', work: () => Promise<void>): Promise<void> {
    this.busy.set(part);

    try {
      await work();
    } catch (error) {
      this.notify.error(toProblem(error).message);
    } finally {
      this.busy.set(null);
    }
  }

  private text(key: string, params?: Record<string, unknown>): string {
    return this.translate.instant(key, params) as string;
  }
}
