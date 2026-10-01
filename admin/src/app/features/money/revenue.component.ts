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
import { MoneyApiService } from '../../core/api/money-api.service';
import { RevenueReport } from '../../core/api/step4.models';
import { provideNeoDates } from '../../shared/date-input';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { FilterBarComponent } from '../../shared/filter-bar.component';
import { MoneyPipe } from '../../shared/format.pipes';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { RevenueChartComponent, RevenueChartDay } from '../../shared/revenue-chart.component';
import { dayString } from './money-shared';

/** The report starts on the last 30 days. */
const defaultDays = 30;

/**
 * "Gəlir hesabatı" (plan section 8A.3; docs/DESIGN_SYSTEM.md section 4):
 * sales, refunds, net and top-ups for a period and a kiosk, a chart by day,
 * and the same by method and by kiosk; downloadable as CSV.
 */
@Component({
  selector: 'app-revenue',
  imports: [
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
    RevenueChartComponent,
    MoneyPipe,
  ],
  providers: [provideNeoDates()],
  template: `
    <div class="neo-page">
      <div class="money__head">
        <app-page-header
          [title]="'revenue.title' | translate"
          [description]="'revenue.description' | translate"
        />
        @if (report(); as r) {
          <button mat-stroked-button type="button" (click)="csv(r)">
            <mat-icon>download</mat-icon>{{ 'revenue.csv' | translate }}
          </button>
        }
      </div>

      <app-filter-bar>
        <mat-form-field class="money__period" subscriptSizing="dynamic">
          <mat-label>{{ 'revenue.period' | translate }}</mat-label>
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
          <mat-form-field>
            <mat-label>{{ 'revenue.kiosk' | translate }}</mat-label>
            <mat-select [ngModel]="kiosk()" (ngModelChange)="kiosk.set($event); load()">
              <mat-option [value]="null">{{ 'payments.allKiosks' | translate }}</mat-option>
              @for (k of kiosks(); track k.id) {
                <mat-option [value]="k.id">{{ k.code }} — {{ k.name }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        }
      </app-filter-bar>

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="revenue.loadFailedEffect" (retry)="load()" />
      }

      @if (report(); as r) {
        <div class="money__stats">
          <div class="neo-card">
            <span class="neo-caption">{{ 'revenue.sales' | translate }}</span>
            <span class="money__stat-value">{{ r.totals.sales | money }}</span>
            <span class="neo-caption">{{
              'revenue.payments' | translate: { count: r.totals.payments }
            }}</span>
          </div>
          <div class="neo-card">
            <span class="neo-caption">{{ 'revenue.refunds' | translate }}</span>
            <span class="money__stat-value">{{ r.totals.refunds | money }}</span>
          </div>
          <div class="neo-card">
            <span class="neo-caption">{{ 'revenue.net' | translate }}</span>
            <span class="money__stat-value">{{ r.totals.net | money }}</span>
            <span class="neo-caption">{{
              'revenue.discounts' | translate: { amount: (r.totals.discounts | money) }
            }}</span>
          </div>
          <div class="neo-card">
            <span class="neo-caption">{{ 'revenue.topUps' | translate }}</span>
            <span class="money__stat-value">{{ r.totals.topUps | money }}</span>
            <span class="neo-caption">{{ 'revenue.topUpsHint' | translate }}</span>
          </div>
        </div>

        @if (r.totals.payments === 0 && r.totals.refunds === 0) {
          <app-empty-state
            icon="query_stats"
            [message]="'revenue.empty' | translate"
            [hint]="'revenue.emptyHint' | translate"
          />
        } @else {
          <section class="neo-card">
            <app-revenue-chart
              [days]="chartDays()"
              [salesLabel]="'revenue.sales' | translate"
              [refundsLabel]="'revenue.refunds' | translate"
              [label]="'revenue.chartLabel' | translate"
              [language]="language()"
            />
          </section>

          <div class="money__grid">
            <section class="neo-card">
              <h2 class="money__card-title">{{ 'revenue.byMethod' | translate }}</h2>
              <dl class="money__rows">
                @for (m of r.methods; track m.method) {
                  <dt>{{ 'money.method.' + m.method | translate }} ({{ m.payments }})</dt>
                  <dd>{{ m.sales | money }}</dd>
                }
              </dl>
            </section>
            <section class="neo-card">
              <h2 class="money__card-title">{{ 'revenue.byKiosk' | translate }}</h2>
              <dl class="money__rows">
                @for (k of r.kiosks; track k.kioskId) {
                  <dt>{{ k.kioskCode ?? k.kioskId }} ({{ k.payments }})</dt>
                  <dd>{{ k.sales | money }}</dd>
                }
              </dl>
            </section>
          </div>

          <div class="neo-table-wrapper">
            <table mat-table class="neo-table" [dataSource]="r.days">
              <ng-container matColumnDef="day">
                <th mat-header-cell *matHeaderCellDef>{{ 'revenue.day' | translate }}</th>
                <td mat-cell *matCellDef="let d">{{ dayLabel(d.day) }}</td>
              </ng-container>
              <ng-container matColumnDef="sales">
                <th mat-header-cell *matHeaderCellDef class="money__number">
                  {{ 'revenue.sales' | translate }}
                </th>
                <td mat-cell *matCellDef="let d" class="money__number">{{ d.sales | money }}</td>
              </ng-container>
              <ng-container matColumnDef="refunds">
                <th mat-header-cell *matHeaderCellDef class="money__number">
                  {{ 'revenue.refunds' | translate }}
                </th>
                <td mat-cell *matCellDef="let d" class="money__number">{{ d.refunds | money }}</td>
              </ng-container>
              <ng-container matColumnDef="net">
                <th mat-header-cell *matHeaderCellDef class="money__number">
                  {{ 'revenue.net' | translate }}
                </th>
                <td mat-cell *matCellDef="let d" class="money__number">{{ d.net | money }}</td>
              </ng-container>
              <tr mat-header-row *matHeaderRowDef="columns"></tr>
              <tr mat-row *matRowDef="let row; columns: columns"></tr>
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
  `,
  styleUrl: './money.scss',
})
export class RevenueComponent implements OnInit {
  private readonly api = inject(MoneyApiService);
  private readonly admin = inject(AdminApiService);
  private readonly translate = inject(TranslateService);

  readonly columns = ['day', 'sales', 'refunds', 'net'];

  readonly from = signal<Date | null>(new Date(Date.now() - (defaultDays - 1) * 86_400_000));
  readonly to = signal<Date | null>(new Date());
  readonly kiosk = signal<string | null>(null);
  readonly kiosks = signal<KioskListItem[]>([]);
  readonly report = signal<RevenueReport | null>(null);
  readonly problem = signal<Problem | null>(null);

  readonly language = computed(() => this.translate.getCurrentLang() ?? 'az');

  readonly chartDays = computed<RevenueChartDay[]>(() =>
    (this.report()?.days ?? []).map((d) => ({
      label: this.dayLabel(d.day),
      sales: d.sales,
      refunds: d.refunds,
    })),
  );

  ngOnInit(): void {
    firstValueFrom(this.admin.listKiosks())
      .then((k) => this.kiosks.set(k))
      .catch(() => this.kiosks.set([]));
    void this.load();
  }

  /** `dd.MM.yyyy` from the API's `yyyy-MM-dd`, the way every date in the panel reads. */
  dayLabel(day: string): string {
    const [year, month, date] = day.split('-');
    return `${date}.${month}.${year}`;
  }

  async load(): Promise<void> {
    const from = this.from();
    const to = this.to();

    if (!from || !to || Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) {
      return;
    }

    this.problem.set(null);

    try {
      this.report.set(
        await firstValueFrom(this.api.revenue(dayString(from), dayString(to), this.kiosk())),
      );
    } catch (error) {
      this.problem.set(toProblem(error));
    }
  }

  /** The days as CSV, with a header in the reader's language; amounts with a dot, as spreadsheets take them. */
  csv(report: RevenueReport): void {
    const t = (key: string): string => this.translate.instant(key) as string;
    const lines = [
      [
        t('revenue.day'),
        t('revenue.sales'),
        t('revenue.refunds'),
        t('revenue.net'),
        t('revenue.paymentsColumn'),
      ].join(';'),
      ...report.days.map((d) =>
        [d.day, d.sales.toFixed(2), d.refunds.toFixed(2), d.net.toFixed(2), d.payments].join(';'),
      ),
    ];
    const blob = new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `neoprint-revenue-${report.from}-${report.to}.csv`;
    link.click();
    URL.revokeObjectURL(url);
  }
}
