import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MoneyApiService } from '../../core/api/money-api.service';
import { PaymentsDashboard } from '../../core/api/step4.models';
import { HubEvents } from '../../core/realtime/hub-events';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { MoneyPipe } from '../../shared/format.pipes';
import { RevenueChartComponent, RevenueChartDay } from '../../shared/revenue-chart.component';
import { StatusChipComponent } from '../../shared/status-chip.component';

/**
 * The dashboard's money card (plan section 8A.3): today's revenue and jobs,
 * the last seven days as a chart, and the money problems waiting for an admin
 * - failed refunds and fiscal receipts first, linked to their lists.
 */
@Component({
  selector: 'app-money-card',
  imports: [
    MatIconModule,
    RouterLink,
    TranslatePipe,
    MoneyPipe,
    RevenueChartComponent,
    StatusChipComponent,
  ],
  template: `
    @if (data(); as d) {
      <section class="neo-card">
        <h2 class="money__card-title">{{ 'moneyCard.title' | translate }}</h2>

        <div class="money__stats">
          <div>
            <span class="neo-caption">{{ 'moneyCard.today' | translate }}</span>
            <span class="money__stat-value">{{ d.today.net | money }}</span>
            <span class="neo-caption">{{
              'moneyCard.todayPayments' | translate: { count: d.today.payments }
            }}</span>
          </div>
          <div>
            <span class="neo-caption">{{ 'moneyCard.week' | translate }}</span>
            <span class="money__stat-value">{{ weekNet() | money }}</span>
          </div>
        </div>

        <div class="alerts">
          @if (d.failedRefunds > 0) {
            <a routerLink="/payments" [queryParams]="{ refund: 'failed' }">
              <app-status-chip
                status="Failed"
                [text]="'moneyCard.failedRefunds' | translate: { count: d.failedRefunds }"
              />
            </a>
          }
          @if (d.failedFiscal > 0) {
            <a routerLink="/fiscal">
              <app-status-chip
                status="Failed"
                [text]="'moneyCard.failedFiscal' | translate: { count: d.failedFiscal }"
              />
            </a>
          }
          @if (d.openMoneyIncidents > 0) {
            <a routerLink="/suspected">
              <app-status-chip
                status="Pending"
                [text]="'moneyCard.incidents' | translate: { count: d.openMoneyIncidents }"
              />
            </a>
          }
          @if (d.failedRefunds === 0 && d.failedFiscal === 0 && d.openMoneyIncidents === 0) {
            <app-status-chip status="Succeeded" [text]="'moneyCard.allClear' | translate" />
          }
        </div>

        <app-revenue-chart
          [days]="days()"
          [salesLabel]="'revenue.sales' | translate"
          [refundsLabel]="'revenue.refunds' | translate"
          [label]="'moneyCard.chartLabel' | translate"
          [language]="language()"
        />
      </section>
    } @else if (failed()) {
      <section class="neo-card">
        <h2 class="money__card-title">{{ 'moneyCard.title' | translate }}</h2>
        <p class="money__error">
          <mat-icon>error</mat-icon>{{ 'moneyCard.loadFailed' | translate }}
        </p>
      </section>
    }
  `,
  styleUrl: './money.scss',
  styles: `
    .alerts {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      margin-block-end: var(--space-4);
    }
  `,
})
export class MoneyCardComponent implements OnInit {
  private readonly api = inject(MoneyApiService);
  private readonly translate = inject(TranslateService);

  readonly data = signal<PaymentsDashboard | null>(null);
  readonly failed = signal(false);

  readonly language = computed(() => this.translate.getCurrentLang() ?? 'az');
  readonly weekNet = computed(() =>
    (this.data()?.lastSevenDays ?? []).reduce((sum, d) => sum + d.net, 0),
  );
  readonly days = computed<RevenueChartDay[]>(() =>
    (this.data()?.lastSevenDays ?? []).map((d) => {
      const [, month, date] = d.day.split('-');
      return { label: `${date}.${month}`, sales: d.sales, refunds: d.refunds };
    }),
  );

  constructor() {
    // Today's takings and the problems waiting for somebody move by
    // themselves: a payment, a refund, a receipt, an incident (T-253).
    liveRefresh({
      events: [
        HubEvents.paymentSucceeded,
        HubEvents.refundStatusChanged,
        HubEvents.fiscalStatusChanged,
        HubEvents.incidentRaised,
        HubEvents.incidentResolved,
      ],
      reload: () => this.load(),
    });
  }

  ngOnInit(): void {
    void this.load();
  }

  /** What is on screen stays when a later fetch fails; only a first one that fails says so. */
  private async load(): Promise<void> {
    try {
      this.data.set(await firstValueFrom(this.api.paymentsDashboard()));
      this.failed.set(false);
    } catch {
      if (!this.data()) {
        this.failed.set(true);
      }
    }
  }
}
