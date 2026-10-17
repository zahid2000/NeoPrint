import { NgTemplateOutlet } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, KioskListItem } from '../../core/api/admin-api.service';
import { AnalyticsApiService } from '../../core/api/analytics-api.service';
import { PilotFormat, PilotReason, PilotReport, PilotStepRow } from '../../core/api/step7.models';
import { HubEvents } from '../../core/realtime/hub-events';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { dayString } from '../money/money-shared';
import { provideNeoDates } from '../../shared/date-input';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { FilterBarComponent } from '../../shared/filter-bar.component';
import { CountPipe, MoneyPipe, formatClock } from '../../shared/format.pipes';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';

/** The report opens on the last 14 days, today included. */
const defaultDays = 14;

/** One step of the funnel as drawn: its bar against the first step, and whether it loses the most. */
interface FunnelBar extends PilotStepRow {
  readonly width: number;
  readonly worst: boolean;
}

/**
 * "Pilot hesabatı" (plan section 2.17; docs/DESIGN_SYSTEM.md, T-214): what
 * the kiosks did over a span of Baku days - the way customers take through
 * the kiosk and where they leave it, the formats that come, why payments and
 * prints fail, money, printing time, paper and toner, incidents. Past days
 * come from the hourly rollup; today is live. Downloadable as CSV.
 */
@Component({
  selector: 'app-pilot-report',
  imports: [
    NgTemplateOutlet,
    FormsModule,
    MatButtonModule,
    MatDatepickerModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    MatTableModule,
    TranslatePipe,
    PageHeaderComponent,
    FilterBarComponent,
    ErrorBannerComponent,
    EmptyStateComponent,
    StatusChipComponent,
    MoneyPipe,
    CountPipe,
  ],
  providers: [provideNeoDates()],
  template: `
    <div class="neo-page">
      <div class="pilot__head">
        <app-page-header
          [title]="'pilot.title' | translate"
          [description]="'pilot.description' | translate"
        />
        @if (report(); as r) {
          <button mat-stroked-button type="button" class="pilot__csv" (click)="csv(r)">
            <mat-icon>download</mat-icon>{{ 'pilot.csv' | translate }}
          </button>
        }
      </div>

      <app-filter-bar>
        <mat-form-field class="pilot__period" subscriptSizing="dynamic">
          <mat-label>{{ 'pilot.period' | translate }}</mat-label>
          <mat-date-range-input [rangePicker]="period">
            <input
              matStartDate
              [placeholder]="'date.hint' | translate"
              [ngModel]="from()"
              (ngModelChange)="from.set($event)"
            />
            <input
              matEndDate
              [placeholder]="'date.hint' | translate"
              [ngModel]="to()"
              (ngModelChange)="to.set($event); load()"
            />
          </mat-date-range-input>
          <mat-datepicker-toggle matIconSuffix [for]="period" />
          <mat-date-range-picker #period />
          <mat-error>{{ 'date.invalid' | translate }}</mat-error>
        </mat-form-field>

        @if (kiosks().length > 0) {
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>{{ 'ops.kiosk' | translate }}</mat-label>
            <mat-select [ngModel]="kiosk()" (ngModelChange)="kiosk.set($event); load()">
              <mat-option [value]="null">{{ 'ops.allKiosks' | translate }}</mat-option>
              @for (k of kiosks(); track k.id) {
                <mat-option [value]="k.id">{{ k.code }} — {{ k.name }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        }
      </app-filter-bar>

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="pilot.loadFailedEffect" (retry)="load()" />
      }

      @if (report(); as r) {
        <p class="neo-caption pilot__computed">
          @if (r.computedAt) {
            {{ 'pilot.computedAt' | translate: { time: clock(r.computedAt) } }}
          }
          @if (includesToday()) {
            {{ 'pilot.todayLive' | translate }}
          }
        </p>

        @if (isEmpty(r)) {
          <app-empty-state
            icon="insights"
            [message]="'pilot.empty' | translate"
            [hint]="'pilot.emptyHint' | translate"
          />
        } @else {
          <div class="pilot__stats">
            <div class="neo-card pilot__stat" data-stat="net">
              <span class="neo-caption">{{ 'pilot.net' | translate }}</span>
              <span class="pilot__stat-value">{{ r.totals.net | money }}</span>
              <span class="neo-caption">{{
                'pilot.salesRefunds'
                  | translate: { sales: (r.totals.sales | money), refunds: (r.totals.refunded | money) }
              }}</span>
            </div>
            <div class="neo-card pilot__stat" data-stat="printed">
              <span class="neo-caption">{{ 'pilot.printsSucceeded' | translate }}</span>
              <span class="pilot__stat-value">{{ r.totals.printsSucceeded | count }}</span>
            </div>
            <div class="neo-card pilot__stat" data-stat="failed">
              <span class="neo-caption">{{ 'pilot.printsFailed' | translate }}</span>
              <span class="pilot__stat-value" [class.pilot__stat-value--danger]="r.totals.printsFailed > 0">
                @if (r.totals.printsFailed > 0) {
                  <mat-icon>error</mat-icon>
                }
                {{ r.totals.printsFailed | count }}
              </span>
            </div>
            <div class="neo-card pilot__stat" data-stat="cancelled">
              <span class="neo-caption">{{ 'pilot.cancelled' | translate }}</span>
              <span class="pilot__stat-value">{{ r.totals.cancelled | count }}</span>
            </div>
            <div class="neo-card pilot__stat" data-stat="refunds">
              <span class="neo-caption">{{ 'pilot.refunds' | translate }}</span>
              <span class="pilot__stat-value">{{ r.totals.refunds | count }}</span>
              <span class="neo-caption">{{ r.totals.refunded | money }}</span>
            </div>
            <div class="neo-card pilot__stat" data-stat="duration">
              <span class="neo-caption">{{ 'pilot.averagePrint' | translate }}</span>
              @if (r.totals.averagePrintSeconds !== undefined && r.totals.averagePrintSeconds !== null) {
                <span class="pilot__stat-value">{{ duration(r.totals.averagePrintSeconds) }}</span>
              } @else {
                <span class="neo-caption pilot__stat-none">{{ 'pilot.noTimedPrint' | translate }}</span>
              }
            </div>
            <div class="neo-card pilot__stat" data-stat="incidents">
              <span class="neo-caption">{{ 'pilot.incidents' | translate }}</span>
              <span class="pilot__stat-value" [class.pilot__stat-value--warning]="r.totals.incidents > 0">
                @if (r.totals.incidents > 0) {
                  <mat-icon>warning</mat-icon>
                }
                {{ r.totals.incidents | count }}
              </span>
              @if (r.incidents.length > 0) {
                <span class="neo-caption">{{ severities(r) }}</span>
              }
            </div>
            <div class="neo-card pilot__stat" data-stat="paper">
              <span class="neo-caption">{{ 'pilot.paper' | translate }}</span>
              <span class="pilot__stat-value">{{ r.totals.paperSheets | count }}</span>
              <span class="neo-caption">{{ 'pilot.sheets' | translate }}</span>
            </div>
          </div>

          <section class="neo-card pilot__funnel">
            <h2 class="pilot__card-title">{{ 'pilot.funnelTitle' | translate }}</h2>
            @if (r.funnel[0].count === 0) {
              <p class="neo-caption">{{ 'pilot.funnelEmpty' | translate }}</p>
            } @else {
              <ol class="pilot__steps">
                @for (s of funnel(); track s.step) {
                  <li class="pilot__step" [attr.data-step]="s.step">
                    <span class="pilot__step-name">{{ 'pilot.step.' + s.step | translate }}</span>
                    <span class="pilot__step-count">{{ s.count | count }}</span>
                    <span class="pilot__bar"><span class="pilot__bar-fill" [style.width.%]="s.width"></span></span>
                    <span class="pilot__loss" [class.pilot__loss--worst]="s.worst">
                      @if (s.loss !== undefined && s.loss !== null) {
                        @if (s.worst) {
                          <mat-icon>trending_down</mat-icon>
                        }
                        {{ s.loss > 0 ? '−' + s.loss : 0 }}%
                      }
                    </span>
                    @if (s.worst) {
                      <span class="pilot__worst-text">{{ 'pilot.worstStep' | translate }}</span>
                    }
                  </li>
                }
              </ol>
            }
            <p class="neo-caption">{{ 'pilot.funnelHint' | translate }}</p>
          </section>

          <div class="pilot__grid">
            <section class="neo-card" data-card="formats">
              <h2 class="pilot__card-title">{{ 'pilot.formatsTitle' | translate }}</h2>
              @if (r.formats.length === 0) {
                <p class="neo-caption">{{ 'pilot.formatsEmpty' | translate }}</p>
              } @else {
                <ng-container
                  *ngTemplateOutlet="formatTable; context: { $implicit: r.formats }"
                ></ng-container>
              }
              @if (r.appFormats; as app) {
                <h3 class="pilot__sub-title">{{ 'pilot.appFormats' | translate }}</h3>
                @if (app.length === 0) {
                  <p class="neo-caption">{{ 'pilot.appFormatsEmpty' | translate }}</p>
                } @else {
                  <ng-container *ngTemplateOutlet="formatTable; context: { $implicit: app }"></ng-container>
                }
              }
            </section>

            <section class="neo-card" data-card="payments">
              <h2 class="pilot__card-title">{{ 'pilot.paymentFailuresTitle' | translate }}</h2>
              @if (r.paymentFailures.length === 0) {
                <p class="neo-caption">{{ 'pilot.paymentFailuresEmpty' | translate }}</p>
              } @else {
                <ng-container
                  *ngTemplateOutlet="reasonTable; context: { $implicit: r.paymentFailures }"
                ></ng-container>
              }
              @if (r.methods.length > 0) {
                <h3 class="pilot__sub-title">{{ 'pilot.byMethod' | translate }}</h3>
                <table class="pilot__table">
                  <thead>
                    <tr>
                      <th>{{ 'pilot.method' | translate }}</th>
                      <th class="pilot__number">{{ 'pilot.started' | translate }}</th>
                      <th class="pilot__number">{{ 'pilot.succeeded' | translate }}</th>
                      <th class="pilot__number">{{ 'pilot.successRate' | translate }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (m of r.methods; track m.method) {
                      <tr>
                        <td>{{ 'money.method.' + m.method | translate }}</td>
                        <td class="pilot__number">{{ m.started | count }}</td>
                        <td class="pilot__number">{{ m.succeeded | count }}</td>
                        <td class="pilot__number">{{ percent(m.succeeded, m.started) }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
            </section>

            <section class="neo-card" data-card="abandoned">
              <h2 class="pilot__card-title">{{ 'pilot.abandonedTitle' | translate }}</h2>
              @if (r.abandoned.length === 0) {
                <p class="neo-caption">{{ 'pilot.abandonedEmpty' | translate }}</p>
              } @else {
                <table class="pilot__table">
                  <thead>
                    <tr>
                      <th>{{ 'pilot.lastScreen' | translate }}</th>
                      <th class="pilot__number">{{ 'pilot.visits' | translate }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (a of r.abandoned; track a.key) {
                      <tr>
                        <td>{{ screenName(a.key) }}</td>
                        <td class="pilot__number">{{ a.count | count }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
            </section>

            <section class="neo-card" data-card="errors">
              <h2 class="pilot__card-title">{{ 'pilot.errorsTitle' | translate }}</h2>
              <h3 class="pilot__sub-title">{{ 'pilot.conversionErrors' | translate }}</h3>
              @if (r.conversionErrors.length === 0) {
                <p class="neo-caption">{{ 'pilot.conversionErrorsEmpty' | translate }}</p>
              } @else {
                <ng-container
                  *ngTemplateOutlet="reasonTable; context: { $implicit: r.conversionErrors }"
                ></ng-container>
              }
              <h3 class="pilot__sub-title">{{ 'pilot.printErrors' | translate }}</h3>
              @if (r.printErrors.length === 0) {
                <p class="neo-caption">{{ 'pilot.printErrorsEmpty' | translate }}</p>
              } @else {
                <ng-container
                  *ngTemplateOutlet="reasonTable; context: { $implicit: r.printErrors }"
                ></ng-container>
              }
            </section>

            <section class="neo-card" data-card="supplies">
              <h2 class="pilot__card-title">{{ 'pilot.suppliesTitle' | translate }}</h2>
              <dl class="pilot__rows">
                @for (p of r.paper; track p.key) {
                  <dt>{{ 'pilot.paperSize' | translate: { size: p.key } }}</dt>
                  <dd>{{ p.count | count }} {{ 'pilot.sheets' | translate }}</dd>
                }
                <dt>{{ 'pilot.testSheets' | translate }}</dt>
                <dd>{{ r.totals.testSheets | count }} {{ 'pilot.sheets' | translate }}</dd>
                @for (t of r.toner; track t.key) {
                  <dt>{{ t.key }}</dt>
                  <dd>{{ 'pilot.points' | translate: { points: t.count } }}</dd>
                }
                <dt>{{ 'pilot.tonerReplaced' | translate }}</dt>
                <dd>{{ r.totals.tonerReplaced | count }}</dd>
              </dl>
              @if (r.toner.length === 0) {
                <p class="neo-caption">{{ 'pilot.tonerUnknown' | translate }}</p>
              }
            </section>

            @if (r.refundReasons.length > 0) {
              <section class="neo-card" data-card="refunds">
                <h2 class="pilot__card-title">{{ 'pilot.refundReasonsTitle' | translate }}</h2>
                <dl class="pilot__rows">
                  @for (x of r.refundReasons; track x.key) {
                    <dt>{{ 'money.refundReason.' + x.key | translate }}</dt>
                    <dd>{{ x.count | count }}</dd>
                  }
                </dl>
              </section>
            }
          </div>

          <div class="neo-table-wrapper">
            <table mat-table class="neo-table" [dataSource]="r.days">
              <ng-container matColumnDef="day">
                <th mat-header-cell *matHeaderCellDef>{{ 'pilot.day' | translate }}</th>
                <td mat-cell *matCellDef="let d" class="neo-nowrap">
                  {{ dayLabel(d.day) }}
                  @if (d.live) {
                    <app-status-chip
                      status="live"
                      toneOverride="info"
                      iconOverride="sensors"
                      [text]="'pilot.live' | translate"
                    />
                  }
                </td>
              </ng-container>
              <ng-container matColumnDef="visits">
                <th mat-header-cell *matHeaderCellDef class="pilot__number">{{ 'pilot.visits' | translate }}</th>
                <td mat-cell *matCellDef="let d" class="pilot__number">{{ d.visits | count }}</td>
              </ng-container>
              <ng-container matColumnDef="orders">
                <th mat-header-cell *matHeaderCellDef class="pilot__number">{{ 'pilot.orders' | translate }}</th>
                <td mat-cell *matCellDef="let d" class="pilot__number">{{ d.orders | count }}</td>
              </ng-container>
              <ng-container matColumnDef="printed">
                <th mat-header-cell *matHeaderCellDef class="pilot__number">
                  {{ 'pilot.printsSucceeded' | translate }}
                </th>
                <td mat-cell *matCellDef="let d" class="pilot__number">{{ d.printsSucceeded | count }}</td>
              </ng-container>
              <ng-container matColumnDef="failed">
                <th mat-header-cell *matHeaderCellDef class="pilot__number">
                  {{ 'pilot.printsFailed' | translate }}
                </th>
                <td mat-cell *matCellDef="let d" class="pilot__number">{{ d.printsFailed | count }}</td>
              </ng-container>
              <ng-container matColumnDef="net">
                <th mat-header-cell *matHeaderCellDef class="pilot__number">{{ 'pilot.net' | translate }}</th>
                <td mat-cell *matCellDef="let d" class="pilot__number">{{ d.net | money }}</td>
              </ng-container>
              <ng-container matColumnDef="paper">
                <th mat-header-cell *matHeaderCellDef class="pilot__number">{{ 'pilot.paper' | translate }}</th>
                <td mat-cell *matCellDef="let d" class="pilot__number">{{ d.paperSheets | count }}</td>
              </ng-container>
              <ng-container matColumnDef="incidents">
                <th mat-header-cell *matHeaderCellDef class="pilot__number">
                  {{ 'pilot.incidents' | translate }}
                </th>
                <td mat-cell *matCellDef="let d" class="pilot__number">{{ d.incidents | count }}</td>
              </ng-container>
              <tr mat-header-row *matHeaderRowDef="columns"></tr>
              <tr mat-row *matRowDef="let row; columns: columns" [attr.data-day]="row.day"></tr>
            </table>
          </div>
        }
      } @else if (!problem()) {
        <div class="neo-card">
          @for (row of [1, 2, 3]; track row) {
            <div class="neo-skeleton neo-skeleton--row"></div>
          }
        </div>
      }
    </div>

    <ng-template #formatTable let-rows>
      <table class="pilot__table">
        <thead>
          <tr>
            <th>{{ 'pilot.format' | translate }}</th>
            <th class="pilot__number">{{ 'pilot.selected' | translate }}</th>
            <th class="pilot__number">{{ 'pilot.notOpened' | translate }}</th>
            <th class="pilot__number">{{ 'pilot.share' | translate }}</th>
          </tr>
        </thead>
        <tbody>
          @for (f of asFormats(rows); track f.format) {
            <tr [attr.data-format]="f.format">
              <td>{{ formatName(f.format) }}</td>
              <td class="pilot__number">{{ f.selected | count }}</td>
              <td class="pilot__number">{{ f.failed | count }}</td>
              <td class="pilot__number">{{ f.share }}%</td>
            </tr>
          }
        </tbody>
      </table>
    </ng-template>

    <ng-template #reasonTable let-rows>
      <table class="pilot__table">
        <thead>
          <tr>
            <th>{{ 'pilot.reason' | translate }}</th>
            <th class="pilot__number">{{ 'pilot.count' | translate }}</th>
          </tr>
        </thead>
        <tbody>
          @for (x of asReasons(rows); track x.key) {
            <tr [attr.data-reason]="x.key">
              <td>
                {{ x.text ?? ('pilot.otherReason' | translate) }}
                @if (!x.text) {
                  <code class="neo-caption pilot__code">{{ x.key }}</code>
                }
              </td>
              <td class="pilot__number">{{ x.count | count }}</td>
            </tr>
          }
        </tbody>
      </table>
    </ng-template>
  `,
  styleUrl: './pilot-report.scss',
})
export class PilotReportComponent implements OnInit {
  private readonly api = inject(AnalyticsApiService);
  private readonly admin = inject(AdminApiService);
  private readonly translate = inject(TranslateService);

  readonly columns = ['day', 'visits', 'orders', 'printed', 'failed', 'net', 'paper', 'incidents'];

  readonly from = signal<Date | null>(new Date(Date.now() - (defaultDays - 1) * 86_400_000));
  readonly to = signal<Date | null>(new Date());
  readonly kiosk = signal<string | null>(null);
  readonly kiosks = signal<KioskListItem[]>([]);
  readonly report = signal<PilotReport | null>(null);
  readonly problem = signal<Problem | null>(null);

  /** Only a span that reaches today changes while the page is open. */
  readonly includesToday = computed(() => this.report()?.days.some((d) => d.live) ?? false);

  readonly funnel = computed<FunnelBar[]>(() => {
    const steps = this.report()?.funnel ?? [];
    const first = steps[0]?.count ?? 0;
    const worst = steps.reduce(
      (most, s) => (s.loss !== undefined && s.loss !== null && s.loss > most ? s.loss : most),
      0,
    );

    return steps.map((s) => ({
      ...s,
      width: first === 0 ? 0 : Math.round((100 * s.count) / first),
      worst: worst > 0 && s.loss === worst,
    }));
  });

  constructor() {
    liveRefresh({
      events: [
        HubEvents.queueUpdated,
        HubEvents.paymentSucceeded,
        HubEvents.refundStatusChanged,
        HubEvents.incidentRaised,
        HubEvents.incidentResolved,
      ],
      reload: () => this.load(true),
      // Past days are the rollup's: nothing that happens now changes them.
      accepts: () => this.includesToday(),
    });
  }

  ngOnInit(): void {
    firstValueFrom(this.admin.listKiosks())
      .then((k) => this.kiosks.set(k))
      .catch(() => this.kiosks.set([]));
    void this.load();
  }

  async load(quiet = false): Promise<void> {
    const from = this.from();
    const to = this.to();

    if (!from || !to || Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) {
      return;
    }

    if (!quiet) {
      this.problem.set(null);
    }

    try {
      this.report.set(await firstValueFrom(this.api.pilot(dayString(from), dayString(to), this.kiosk())));
      this.problem.set(null);
    } catch (error) {
      if (!quiet) {
        this.problem.set(toProblem(error));
      }
    }
  }

  isEmpty(r: PilotReport): boolean {
    const t = r.totals;
    return (
      t.visits === 0 &&
      t.orders === 0 &&
      t.printsSucceeded === 0 &&
      t.printsFailed === 0 &&
      t.paymentsStarted === 0 &&
      t.refunds === 0 &&
      t.incidents === 0 &&
      t.paperSheets === 0 &&
      (r.appFormats?.length ?? 0) === 0
    );
  }

  /** `dd.MM.yyyy` from `yyyy-MM-dd`, the way every date in the panel reads. */
  dayLabel(day: string): string {
    const [year, month, date] = day.split('-');
    return `${date}.${month}.${year}`;
  }

  clock(value: string): string {
    return formatClock(value);
  }

  /** "1 dəq 20 san" - never "80 san" for a print over a minute. */
  duration(seconds: number): string {
    const minutes = Math.floor(seconds / 60);
    const rest = seconds % 60;

    return minutes > 0
      ? this.translate.instant('pilot.durationMinutes', { minutes, seconds: rest })
      : this.translate.instant('pilot.durationSeconds', { seconds: rest });
  }

  percent(part: number, whole: number): string {
    return whole === 0 ? '0%' : `${Math.round((1000 * part) / whole) / 10}%`;
  }

  severities(r: PilotReport): string {
    return r.incidents
      .map((i) => `${this.translate.instant('money.incidentSeverity.' + i.key)} ${i.count}`)
      .join(' · ');
  }

  /** A kiosk screen's name; one the panel does not know reads as "another screen", never as its code. */
  screenName(key: string): string {
    const name = 'pilot.screen.' + key;
    const text = this.translate.instant(name) as string;
    return text === name ? (this.translate.instant('pilot.screen.Unknown') as string) : text;
  }

  formatName(format: string): string {
    return format === '?' ? (this.translate.instant('pilot.unknownFormat') as string) : format.toUpperCase();
  }

  asFormats(rows: unknown): readonly PilotFormat[] {
    return rows as readonly PilotFormat[];
  }

  asReasons(rows: unknown): readonly PilotReason[] {
    return rows as readonly PilotReason[];
  }

  /**
   * The report as CSV, headed in the reader's language: the days first,
   * then each part under a line of its own. Semicolons and a byte-order mark,
   * as spreadsheets in this region open them; amounts with a dot.
   */
  csv(report: PilotReport): void {
    const t = (key: string, params?: Record<string, unknown>): string => this.translate.instant(key, params) as string;
    const cell = (value: string | number): string => {
      const text = String(value);
      return /[;"\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
    };
    const row = (...values: (string | number)[]): string => values.map(cell).join(';');
    const lines: string[] = [
      row(
        t('pilot.day'),
        t('pilot.visits'),
        t('pilot.orders'),
        t('pilot.printsSucceeded'),
        t('pilot.printsFailed'),
        t('pilot.net'),
        t('pilot.paper'),
        t('pilot.incidents'),
      ),
      ...report.days.map((d) =>
        row(d.day, d.visits, d.orders, d.printsSucceeded, d.printsFailed, d.net.toFixed(2), d.paperSheets, d.incidents),
      ),
      '',
      row(t('pilot.funnelTitle'), t('pilot.count'), t('pilot.lossColumn')),
      ...report.funnel.map((s) => row(t('pilot.step.' + s.step), s.count, s.loss ?? '')),
      '',
      row(t('pilot.formatsTitle'), t('pilot.selected'), t('pilot.notOpened'), t('pilot.share')),
      ...report.formats.map((f) => row(this.formatName(f.format), f.selected, f.failed, f.share)),
      ...(report.appFormats ?? []).map((f) =>
        row(`${t('pilot.appFormats')}: ${this.formatName(f.format)}`, f.selected, f.failed, f.share),
      ),
      '',
      row(t('pilot.paymentFailuresTitle'), t('pilot.count')),
      ...report.paymentFailures.map((x) => row(x.text ?? x.key, x.count)),
      ...report.methods.map((m) =>
        row(`${t('pilot.byMethod')}: ${t('money.method.' + m.method)}`, m.started, m.succeeded),
      ),
      '',
      row(t('pilot.abandonedTitle'), t('pilot.visits')),
      ...report.abandoned.map((a) => row(this.screenName(a.key), a.count)),
      '',
      row(t('pilot.errorsTitle'), t('pilot.count')),
      ...report.conversionErrors.map((x) => row(`${t('pilot.conversionErrors')}: ${x.text ?? x.key}`, x.count)),
      ...report.printErrors.map((x) => row(`${t('pilot.printErrors')}: ${x.text ?? x.key}`, x.count)),
      '',
      row(t('pilot.suppliesTitle'), t('pilot.count')),
      ...report.paper.map((p) => row(t('pilot.paperSize', { size: p.key }), p.count)),
      row(t('pilot.testSheets'), report.totals.testSheets),
      ...report.toner.map((x) => row(x.key, x.count)),
      row(t('pilot.tonerReplaced'), report.totals.tonerReplaced),
      '',
      row(t('pilot.net'), report.totals.net.toFixed(2)),
      row(t('pilot.refunds'), report.totals.refunds),
      row(t('pilot.cancelled'), report.totals.cancelled),
      row(t('pilot.averagePrint'), report.totals.averagePrintSeconds ?? ''),
    ];

    const code = this.kiosks().find((k) => k.id === report.kioskId)?.code;
    const blob = new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `neoprint-pilot-${report.from}-${report.to}${code ? `-${code}` : ''}.csv`;
    link.click();
    URL.revokeObjectURL(url);
  }
}
