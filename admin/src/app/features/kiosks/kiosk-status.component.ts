import { Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { firstValueFrom, map } from 'rxjs';

import { MonitoringApiService } from '../../core/api/monitoring-api.service';
import { KioskTelemetry, KioskTelemetryPoint, KioskTelemetrySample } from '../../core/api/step7.models';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { TelemetryLiveStore } from '../../core/realtime/telemetry-live.store';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { formatBytes, formatClock, formatCount, formatDateTime, formatTime } from '../../shared/format.pipes';
import { LineChartComponent, LineSeries } from '../../shared/line-chart.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent, StatusTone } from '../../shared/status-chip.component';

/** A row of a group: its name, its value written out, and the limit it is over. */
interface MetricRow {
  readonly key: string;
  readonly value: string | null;
  readonly breach?: string | null;
}

interface MetricGroup {
  readonly key: string;
  readonly rows: readonly MetricRow[];
}

interface HealthChip {
  readonly key: string;
  readonly tone: StatusTone;
  readonly text: string;
}

interface Chart {
  readonly key: string;
  readonly series: readonly LineSeries[];
  readonly threshold: number | null;
  readonly format: (value: number) => string;
  readonly summary: string | null;
}

type Range = '24h' | '7d';

const gigabyte = 1024 ** 3;

/** The limits of plan section 7.9, drawn on the graphs. */
const limits = { memoryPercent: 90, latencyMs: 2000, diskGb: 2 };

/**
 * Kiosk detail → "Cari vəziyyət" (plan sections 7.9, 8A.3): what the kiosk's
 * computer last said about itself, live, and its graphs over the last day or
 * week. The reading comes from {@link TelemetryLiveStore}; the graphs are
 * this tab's own.
 */
@Component({
  selector: 'app-kiosk-status',
  imports: [
    FormsModule,
    MatButtonToggleModule,
    MatIconModule,
    TranslatePipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    LineChartComponent,
    StatusChipComponent,
  ],
  template: `
    <p class="neo-muted neo-body-small">{{ 'monitoring.status.description' | translate }}</p>

    @if (problem(); as problem) {
      <app-error-banner [problem]="problem" effect="monitoring.status.loadFailedEffect" (retry)="load()" />
    }

    @if (reading() === undefined && !problem()) {
      <div class="neo-skeleton neo-skeleton--block"></div>
    } @else if (reading(); as current) {
      @if (current.sample; as sample) {
        <p class="neo-caption status__at">
          {{ 'monitoring.status.reportedAt' | translate: { time: clock(sample.timestamp) } }}
        </p>

        @if (stale()) {
          <div class="neo-notice" role="status">
            <mat-icon aria-hidden="true">timer_off</mat-icon>
            <span>{{ 'monitoring.status.stale' | translate: { time: at(sample.timestamp) } }}</span>
          </div>
        } @else {
          <div class="status__health" role="list" [attr.aria-label]="'monitoring.status.health' | translate">
            @for (chip of health(); track chip.key) {
              <span role="listitem">
                <app-status-chip [status]="chip.key" [toneOverride]="chip.tone" [text]="chip.text" />
              </span>
            }
          </div>
        }

        <div class="status__groups">
          @for (group of groups(); track group.key) {
            <section class="neo-card status__group">
              <h3>{{ 'monitoring.group.' + group.key | translate }}</h3>
              <dl>
                @for (row of group.rows; track row.key) {
                  <div class="neo-metric" [class.neo-metric--warning]="row.breach">
                    <dt>{{ 'monitoring.metric.' + row.key | translate }}</dt>
                    <dd>
                      @if (row.value === null) {
                        <span class="neo-caption">{{ 'monitoring.notReported' | translate }}</span>
                      } @else {
                        @if (row.breach) {
                          <mat-icon aria-hidden="true">warning</mat-icon>
                        }
                        <span>{{ row.value }}</span>
                        @if (row.breach) {
                          <span class="neo-caption">{{ row.breach }}</span>
                        }
                      }
                    </dd>
                  </div>
                }
              </dl>
            </section>
          }
        </div>
      } @else {
        <app-empty-state
          icon="query_stats"
          [message]="'monitoring.status.never' | translate"
          [hint]="'monitoring.status.neverHint' | translate"
        />
      }

      <section class="neo-card status__charts">
        <div class="status__charts-head">
          <h3>{{ 'monitoring.charts.title' | translate }}</h3>
          <mat-button-toggle-group
            [attr.aria-label]="'monitoring.charts.range' | translate"
            [ngModel]="range()"
            (ngModelChange)="setRange($event)"
          >
            <mat-button-toggle value="24h">{{ 'monitoring.charts.day' | translate }}</mat-button-toggle>
            <mat-button-toggle value="7d">{{ 'monitoring.charts.week' | translate }}</mat-button-toggle>
          </mat-button-toggle-group>
        </div>

        @if (historyProblem(); as problem) {
          <app-error-banner [problem]="problem" effect="monitoring.charts.loadFailedEffect" (retry)="loadHistory()" />
        } @else if (history() === null) {
          <div class="neo-skeleton neo-skeleton--block"></div>
        } @else {
          <div class="status__chart-grid">
            @for (chart of charts(); track chart.key) {
              <figure class="status__chart">
                <figcaption>{{ 'monitoring.chart.' + chart.key | translate }}</figcaption>
                @if (chart.summary === null) {
                  <p class="neo-caption">{{ 'monitoring.charts.empty' | translate }}</p>
                } @else {
                  <app-line-chart
                    [labels]="labels()"
                    [series]="chart.series"
                    [threshold]="chart.threshold"
                    [thresholdLabel]="'monitoring.charts.limit' | translate"
                    [format]="chart.format"
                    [label]="'monitoring.chart.' + chart.key | translate"
                  />
                  <p class="neo-caption">{{ chart.summary }}</p>
                }
              </figure>
            }
          </div>
        }
      </section>
    }
  `,
  styles: `
    h3 {
      margin: 0 0 var(--space-3);
    }

    .status__at {
      margin: var(--space-3) 0;
    }

    .status__health {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      margin-block-end: var(--space-4);
    }

    .status__groups,
    .status__chart-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
      gap: var(--space-4);
    }

    .status__group,
    .status__charts {
      padding: var(--space-4);
    }

    .status__group dl {
      margin: 0;
    }

    .status__charts {
      margin-block-start: var(--space-6);
    }

    .status__charts-head {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-3);
      align-items: center;
      justify-content: space-between;
      margin-block-end: var(--space-4);
    }

    .status__charts-head h3 {
      margin: 0;
    }

    .status__chart {
      margin: 0;
    }

    .status__chart figcaption {
      margin-block-end: var(--space-2);
      font-weight: var(--font-weight-semibold);
    }
  `,
})
export class KioskStatusComponent implements OnInit {
  private readonly api = inject(MonitoringApiService);
  private readonly store = inject(TelemetryLiveStore);
  private readonly translate = inject(TranslateService);
  private readonly destroyRef = inject(DestroyRef);

  readonly kioskId = input.required<string>();

  readonly problem = signal<Problem | null>(null);
  readonly range = signal<Range>('24h');
  readonly history = signal<readonly KioskTelemetryPoint[] | null>(null);
  readonly historyProblem = signal<Problem | null>(null);

  /** The reader's language: the written-out values follow it. */
  private readonly language = toSignal(this.translate.onLangChange.pipe(map((change) => change.lang)), {
    initialValue: this.translate.getCurrentLang(),
  });

  /** Ticks, so a report that stops coming turns stale without an event. */
  private readonly now = signal(Date.now());

  /** The kiosk's last report, from the panel's one copy of it; undefined until fetched. */
  readonly reading = computed<KioskTelemetry | undefined>(() => this.store.telemetryOf(this.kioskId()));

  readonly stale = computed(() => {
    const reading = this.reading();
    const sample = reading?.sample;

    if (!reading || !sample) {
      return false;
    }

    return reading.stale || this.now() - Date.parse(sample.timestamp) > reading.intervalSeconds * 3 * 1000;
  });

  readonly health = computed<HealthChip[]>(() => {
    const sample = this.reading()?.sample;
    this.language();

    if (!sample) {
      return [];
    }

    const breaches = sample.breaches ?? {};
    const chip = (key: string, known: boolean, breached: boolean, down = false): HealthChip => ({
      key,
      tone: !known ? 'neutral' : down ? 'danger' : breached ? 'warning' : 'success',
      text: this.text(`monitoring.health.${key}.${!known ? 'unknown' : down ? 'down' : breached ? 'breached' : 'ok'}`),
    });

    return [
      chip('disk', sample.system?.systemDiskFreeBytes != null, !!breaches['disk']),
      chip('memory', sample.system?.memoryTotalBytes != null, !!breaches['memory']),
      chip('latency', sample.network?.backendLatencyMs != null, !!breaches['latency']),
      chip('hub', sample.network?.hubConnected != null, false, sample.network?.hubConnected === false || !!breaches['hub']),
      chip('clock', sample.system?.clockOffsetMs != null, !!breaches['clock']),
    ];
  });

  readonly groups = computed<MetricGroup[]>(() => {
    const sample = this.reading()?.sample;
    this.language();

    return sample ? this.describe(sample) : [];
  });

  readonly labels = computed(() =>
    (this.history() ?? []).map((point) => (this.range() === '24h' ? formatTime(point.at) : formatDateTime(point.at))),
  );

  readonly charts = computed<Chart[]>(() => {
    const points = this.history() ?? [];
    this.language();
    const percent = (value: number): string => `${Math.round(value)}%`;
    const milliseconds = (value: number): string => this.text('monitoring.unit.ms', { value: Math.round(value) });
    const gigabytes = (value: number): string => this.text('monitoring.unit.gb', { value: value.toFixed(1) });
    const chart = (
      key: string,
      values: (number | null)[],
      threshold: number | null,
      format: (value: number) => string,
    ): Chart => ({
      key,
      series: [{ label: this.text(`monitoring.chart.${key}`), values }],
      threshold,
      format,
      summary: this.summary(values, format),
    });

    return [
      chart('cpu', points.map((point) => point.cpuPercent ?? null), null, percent),
      chart('memory', points.map((point) => point.memoryUsedPercent ?? null), limits.memoryPercent, percent),
      chart('latency', points.map((point) => point.backendLatencyMs ?? null), limits.latencyMs, milliseconds),
      chart(
        'disk',
        points.map((point) => (point.systemDiskFreeBytes == null ? null : point.systemDiskFreeBytes / gigabyte)),
        limits.diskGb,
        gigabytes,
      ),
    ];
  });

  constructor() {
    const tick = setInterval(() => this.now.set(Date.now()), 30_000);
    this.destroyRef.onDestroy(() => clearInterval(tick));

    // The graphs are not redrawn with every report: they follow the
    // connection coming back, and the range the reader picks.
    liveRefresh({ events: [], reload: () => this.loadHistory(true) });
  }

  ngOnInit(): void {
    void this.load();
    void this.loadHistory();
  }

  async load(): Promise<void> {
    this.problem.set(null);
    this.problem.set(await this.store.watchTelemetry(this.kioskId(), this.destroyRef));
  }

  setRange(range: Range): void {
    this.range.set(range);
    this.history.set(null);
    void this.loadHistory();
  }

  async loadHistory(quiet = false): Promise<void> {
    const to = new Date();
    const from = new Date(to.getTime() - (this.range() === '24h' ? 24 : 24 * 7) * 3_600_000);

    if (!quiet) {
      this.historyProblem.set(null);
    }

    try {
      this.history.set(await firstValueFrom(this.api.kioskTelemetryHistory(this.kioskId(), from, to)));
    } catch (error) {
      if (!quiet) {
        this.historyProblem.set(toProblem(error));
      }
    }
  }

  at(timestamp: string): string {
    return formatDateTime(timestamp, { today: this.text('common.today'), yesterday: this.text('common.yesterday') });
  }

  /** The last report's time to the second: a new one every 30 seconds shows. */
  clock(timestamp: string): string {
    return formatClock(timestamp);
  }

  /** "Son: X · Orta: Y · Maks: Z", or null when the range has no value at all. */
  private summary(values: (number | null)[], format: (value: number) => string): string | null {
    const known = values.filter((value): value is number => value !== null);

    if (known.length === 0) {
      return null;
    }

    return this.text('monitoring.charts.summary', {
      last: format(known[known.length - 1]),
      average: format(known.reduce((sum, value) => sum + value, 0) / known.length),
      max: format(Math.max(...known)),
    });
  }

  /** Plan section 7.9's groups, each value written out in the reader's language. */
  private describe(sample: KioskTelemetrySample): MetricGroup[] {
    const language = this.translate.getCurrentLang();
    const breaches = sample.breaches ?? {};
    const { app, system, network, peripherals, work, services } = sample;
    const when = (value: string | null | undefined): string | null => (value ? this.at(value) : null);
    const count = (value: number | null | undefined): string | null => (value == null ? null : formatCount(value, language));
    const bytes = (value: number | null | undefined): string | null => (value == null ? null : formatBytes(value, language));
    const yesNo = (value: boolean | null | undefined, yes: string, no: string): string | null =>
      value == null ? null : this.text(value ? yes : no);
    const since = (condition: string): string | null =>
      breaches[condition] ? this.text(`monitoring.breach.${condition}`, { since: this.at(breaches[condition]) }) : null;

    const memory =
      system?.memoryUsedBytes != null && system.memoryTotalBytes != null
        ? `${formatBytes(system.memoryUsedBytes, language)} / ${formatBytes(system.memoryTotalBytes, language)} (${Math.round(
            system.memoryUsedPercent ?? (system.memoryUsedBytes / system.memoryTotalBytes) * 100,
          )}%)`
        : null;

    return [
      {
        key: 'app',
        rows: [
          { key: 'version', value: app?.version ?? null },
          { key: 'startedAt', value: when(app?.startedAt) },
          { key: 'screen', value: app?.screen ?? null },
          { key: 'session', value: app?.session ? this.text(`monitoring.session.${app.session}`) : null },
          { key: 'lastCrashAt', value: app?.lastCrashAt === undefined ? null : (when(app?.lastCrashAt) ?? this.text('monitoring.value.never')) },
        ],
      },
      {
        key: 'system',
        rows: [
          { key: 'os', value: system?.os ? [system.os, system.osVersion].filter(Boolean).join(' ') : null },
          { key: 'hostname', value: system?.hostname ?? null },
          { key: 'cpu', value: system?.cpuPercent == null ? null : `${Math.round(system.cpuPercent)}%` },
          { key: 'memory', value: memory, breach: since('memory') },
          { key: 'systemDisk', value: bytes(system?.systemDiskFreeBytes), breach: since('disk') },
          { key: 'spoolDisk', value: bytes(system?.spoolDiskFreeBytes) },
          { key: 'uptime', value: system?.uptimeSeconds == null ? null : this.duration(system.uptimeSeconds) },
          {
            key: 'clockOffset',
            value:
              system?.clockOffsetMs == null
                ? null
                : this.text('monitoring.value.seconds', {
                    // A few milliseconds behind is "0.0", not "-0.0".
                    value: (Math.round(system.clockOffsetMs / 100) / 10 || 0).toFixed(1),
                  }),
            breach: since('clock'),
          },
        ],
      },
      {
        key: 'network',
        rows: [
          {
            key: 'connection',
            value: network?.connectionType
              ? this.text(`monitoring.connection.${network.connectionType}`) +
                (network.wifiSignalPercent == null ? '' : ` · ${network.wifiSignalPercent}%`)
              : null,
          },
          { key: 'localIp', value: network?.localIp ?? null },
          {
            key: 'latency',
            value: network?.backendLatencyMs == null ? null : this.text('monitoring.unit.ms', { value: network.backendLatencyMs }),
            breach: since('latency'),
          },
          {
            key: 'hub',
            value: yesNo(network?.hubConnected, 'monitoring.value.connected', 'monitoring.value.disconnected'),
            breach: since('hub'),
          },
          { key: 'hubLastReconnectAt', value: when(network?.hubLastReconnectAt) },
        ],
      },
      {
        key: 'peripherals',
        rows: [
          { key: 'flash', value: yesNo(peripherals?.flashInserted, 'monitoring.value.inserted', 'monitoring.value.notInserted') },
          { key: 'qrScanner', value: when(peripherals?.qrScannerLastReadAt) },
          {
            key: 'screenSize',
            value:
              peripherals?.screenWidth != null && peripherals.screenHeight != null
                ? `${peripherals.screenWidth} × ${peripherals.screenHeight}`
                : null,
          },
        ],
      },
      {
        key: 'work',
        rows: [
          { key: 'queueLength', value: count(work?.queueLength) },
          { key: 'currentJob', value: work?.currentJob ?? (work ? this.text('monitoring.value.none') : null) },
          { key: 'jobsToday', value: count(work?.jobsToday) },
          { key: 'sheetsToday', value: count(work?.sheetsToday) },
          { key: 'spoolFiles', value: count(work?.spoolFiles) },
          { key: 'failedFlashUploads', value: count(work?.failedFlashUploads) },
          { key: 'pendingCommands', value: count(work?.pendingCommands) },
        ],
      },
      {
        key: 'services',
        rows: [
          { key: 'libreOffice', value: yesNo(services?.libreOffice, 'monitoring.value.available', 'monitoring.value.missing') },
          { key: 'ghostscript', value: yesNo(services?.ghostscript, 'monitoring.value.available', 'monitoring.value.missing') },
          { key: 'configurationAt', value: when(services?.configurationAt) },
          { key: 'featureFlags', value: services?.featureFlags ?? null },
        ],
      },
    ];
  }

  /** "3 gün 4 saat", "2 saat 5 dəq", "40 dəq". */
  private duration(seconds: number): string {
    const days = Math.floor(seconds / 86_400);
    const hours = Math.floor((seconds % 86_400) / 3_600);
    const minutes = Math.floor((seconds % 3_600) / 60);

    return days > 0
      ? this.text('monitoring.duration.days', { days, hours })
      : hours > 0
        ? this.text('monitoring.duration.hours', { hours, minutes })
        : this.text('monitoring.duration.minutes', { minutes });
  }

  private text(key: string, params?: Record<string, unknown>): string {
    return this.translate.instant(key, params) as string;
  }
}
