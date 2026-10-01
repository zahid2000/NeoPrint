import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MoneyApiService } from '../../core/api/money-api.service';
import { PaymentDetail, PaymentRefund } from '../../core/api/step4.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { confirmAction } from '../../shared/confirm.dialog';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe, MoneyPipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { RefundDialogComponent, RefundDialogData } from './refund-dialog.component';

/**
 * The payment page (plan section 8A.3, "ödəniş detalı"): what was paid and
 * how, the job, the customer, the receipt and the fiscal receipt, every
 * refund, and the full history; "Qaytar" for what is still refundable.
 */
@Component({
  selector: 'app-payment-detail',
  imports: [
    MatButtonModule,
    MatIconModule,
    MatSelectModule,
    RouterLink,
    TranslatePipe,
    PageHeaderComponent,
    ErrorBannerComponent,
    StatusChipComponent,
    DateTimePipe,
    MoneyPipe,
  ],
  template: `
    <div class="neo-page">
      <app-page-header
        [title]="'paymentDetail.title' | translate"
        [description]="'paymentDetail.description' | translate"
      />

      <a mat-button routerLink="/payments"
        ><mat-icon>arrow_back</mat-icon>{{ 'paymentDetail.back' | translate }}</a
      >

      @if (problem(); as problem) {
        <app-error-banner
          [problem]="problem"
          effect="paymentDetail.loadFailedEffect"
          (retry)="load()"
        />
      }

      @if (detail(); as d) {
        <div class="money__head">
          <div class="money__headline">
            <p class="money__amount">{{ d.payment.amount | money }}</p>
            <app-status-chip
              [status]="d.payment.status"
              [text]="'money.status.' + d.payment.status | translate"
            />
            <span class="neo-caption"
              >{{ 'money.method.' + d.payment.method | translate }} ·
              {{ d.payment.createdAt | dateTime }}</span
            >
          </div>
          @if (canRefund() && d.refundable > 0) {
            <button mat-flat-button type="button" (click)="refund()">
              <mat-icon>undo</mat-icon>{{ 'paymentDetail.refund' | translate }}
            </button>
          }
        </div>

        @if (d.payment.failureReason) {
          <p class="money__error"><mat-icon>error</mat-icon>{{ d.payment.failureReason }}</p>
        }

        <div class="money__grid">
          <section class="neo-card">
            <h2 class="money__card-title">{{ 'paymentDetail.split' | translate }}</h2>
            <dl class="money__rows">
              <dt>{{ 'paymentDetail.gross' | translate }}</dt>
              <dd>{{ d.split.gross + d.split.volumeDiscount | money }}</dd>
              @if (d.split.volumeDiscount > 0) {
                <dt>{{ 'paymentDetail.volumeDiscount' | translate }}</dt>
                <dd>−{{ d.split.volumeDiscount | money }}</dd>
              }
              @if (d.split.promoDiscount > 0) {
                <dt>{{ 'paymentDetail.promo' | translate: { code: d.split.promoCode } }}</dt>
                <dd>−{{ d.split.promoDiscount | money }}</dd>
              }
              @if (d.split.bonusPages > 0) {
                <dt>{{ 'paymentDetail.bonus' | translate: { pages: d.split.bonusPages } }}</dt>
                <dd>−{{ d.split.bonusValue | money }}</dd>
              }
              @if (d.split.wallet > 0) {
                <dt>{{ 'paymentDetail.fromWallet' | translate }}</dt>
                <dd>{{ d.split.wallet | money }}</dd>
              }
              @if (d.split.external > 0) {
                <dt>{{ 'paymentDetail.external' | translate }}</dt>
                <dd>{{ d.split.external | money }}</dd>
              }
              <dt class="money__total">{{ 'paymentDetail.paid' | translate }}</dt>
              <dd class="money__total">{{ d.payment.amount | money }}</dd>
              @if (d.payment.refunded > 0) {
                <dt>{{ 'paymentDetail.refunded' | translate }}</dt>
                <dd>−{{ d.payment.refunded | money }}</dd>
              }
            </dl>
            @if (d.providerRef) {
              <p class="neo-caption">
                {{
                  'paymentDetail.providerRef'
                    | translate: { provider: d.provider, ref: d.providerRef }
                }}
              </p>
            }
          </section>

          <section class="neo-card">
            <h2 class="money__card-title">{{ 'paymentDetail.order' | translate }}</h2>
            @if (d.job; as job) {
              <dl class="money__rows">
                <dt>{{ 'paymentDetail.jobCode' | translate }}</dt>
                <dd>
                  <a [routerLink]="['/print-jobs', job.id]">{{ job.jobCode }}</a>
                </dd>
                <dt>{{ 'paymentDetail.jobStatus' | translate }}</dt>
                <dd>
                  <app-status-chip
                    [status]="job.status"
                    [text]="'jobs.status.' + job.status | translate"
                  />
                </dd>
                <dt>{{ 'paymentDetail.file' | translate }}</dt>
                <dd>{{ job.fileName ?? ('jobs.noFile' | translate) }}</dd>
                <dt>{{ 'paymentDetail.pages' | translate }}</dt>
                <dd>{{ job.pages }} × {{ job.copies }} · {{ job.paper }}</dd>
                <dt>{{ 'paymentDetail.kiosk' | translate }}</dt>
                <dd>{{ d.payment.kioskCode }} {{ d.kioskName }}</dd>
              </dl>
            } @else {
              <p class="neo-muted">{{ 'money.purpose.' + d.payment.purpose | translate }}</p>
            }
          </section>

          <section class="neo-card">
            <h2 class="money__card-title">{{ 'paymentDetail.customer' | translate }}</h2>
            @if (d.user; as user) {
              <dl class="money__rows">
                <dt>{{ 'paymentDetail.email' | translate }}</dt>
                <dd>
                  <a [routerLink]="['/customers', user.id]">{{ user.email }}</a>
                </dd>
                <dt>{{ 'paymentDetail.publicId' | translate }}</dt>
                <dd>{{ user.publicId }}</dd>
              </dl>
            } @else {
              <p class="neo-muted">{{ 'paymentDetail.anonymous' | translate }}</p>
            }
          </section>

          <section class="neo-card">
            <h2 class="money__card-title">{{ 'paymentDetail.receipts' | translate }}</h2>
            <dl class="money__rows">
              <dt>{{ 'paymentDetail.receipt' | translate }}</dt>
              <dd>{{ d.receiptNumber ?? ('paymentDetail.noReceipt' | translate) }}</dd>
              @for (f of d.fiscal; track f.id) {
                <dt>{{ 'money.fiscalType.' + f.type | translate }} · {{ f.amount | money }}</dt>
                <dd>
                  <app-status-chip
                    [status]="f.status"
                    [text]="'money.fiscalStatus.' + f.status | translate"
                  />
                </dd>
              }
            </dl>
            @if (d.fiscal.length === 0) {
              <p class="neo-caption">{{ 'paymentDetail.noFiscal' | translate }}</p>
            }
          </section>
        </div>

        <section class="neo-card">
          <h2 class="money__card-title">{{ 'paymentDetail.refunds' | translate }}</h2>
          @for (r of d.refunds; track r.id) {
            <div class="money__item">
              <div class="money__item-head">
                <strong>{{ r.amount | money }}</strong>
                <span
                  >{{ 'money.destination.' + r.destination | translate }} ·
                  {{ 'money.refundReason.' + r.reason | translate }}</span
                >
                <app-status-chip
                  [status]="r.status"
                  [text]="'money.refundStatus.' + r.status | translate"
                />
              </div>
              <span class="neo-caption"
                >{{ r.createdAt | dateTime }} ·
                {{ 'money.initiatedBy.' + r.initiatedBy | translate }}</span
              >
              @if (r.voucherCode) {
                <p>{{ 'paymentDetail.voucher' | translate: { code: r.voucherCode } }}</p>
              }
              @if (r.lastError && r.status === 'Failed') {
                <p class="money__error"><mat-icon>error</mat-icon>{{ r.lastError }}</p>
              }
              @if (r.status === 'Failed' && canRetry()) {
                <div class="money__actions">
                  <mat-select
                    class="destination"
                    [value]="retryDestination(r)"
                    (selectionChange)="retryTo.set($event.value)"
                  >
                    @for (option of retryOptions(); track option) {
                      <mat-option [value]="option">{{
                        'money.destination.' + option | translate
                      }}</mat-option>
                    }
                  </mat-select>
                  <button mat-stroked-button type="button" (click)="retry(r)">
                    {{ 'paymentDetail.retry' | translate }}
                  </button>
                </div>
              }
            </div>
          } @empty {
            <p class="neo-muted">{{ 'paymentDetail.noRefunds' | translate }}</p>
          }
        </section>

        <section class="neo-card timeline">
          <h2 class="money__card-title">{{ 'paymentDetail.timeline' | translate }}</h2>
          <dl class="money__timeline">
            @for (e of d.timeline; track $index) {
              <dt>{{ e.at | dateTime }}</dt>
              <dd>
                {{ eventText(e.event) }}
                @if (e.details) {
                  <span class="neo-caption">{{ e.details }}</span>
                }
              </dd>
            }
          </dl>
        </section>
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
  styles: `
    .timeline {
      margin-block-start: var(--space-4);
    }

    .destination {
      width: 200px;
    }
  `,
})
export class PaymentDetailComponent implements OnInit {
  private readonly api = inject(MoneyApiService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly translate = inject(TranslateService);

  /** The route's `:id`, bound by the router. */
  readonly id = input.required<string>();

  readonly detail = signal<PaymentDetail | null>(null);
  readonly problem = signal<Problem | null>(null);
  readonly retryTo = signal<string | null>(null);

  readonly canRefund = computed(() => this.auth.can(Permissions.paymentsRefund));
  readonly canRetry = computed(() => this.auth.can(Permissions.refundsRetry));

  /** A failed refund goes again to the card, or where the rules allow: balance for an account, voucher for anonymous. */
  readonly retryOptions = computed(() =>
    this.detail()?.user ? ['OriginalMethod', 'Wallet'] : ['OriginalMethod', 'Voucher'],
  );

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.problem.set(null);

    try {
      this.detail.set(await firstValueFrom(this.api.getPayment(this.id())));
    } catch (error) {
      this.problem.set(toProblem(error));
    }
  }

  /** The event's name in the reader's language; one the panel does not know yet shows as it is. */
  eventText(event: string): string {
    const key = `money.event.${event}`;
    const text = this.translate.instant(key) as string;
    return text === key ? event : text;
  }

  retryDestination(refund: PaymentRefund): string {
    return this.retryTo() ?? refund.destination;
  }

  async refund(): Promise<void> {
    const options = await firstValueFrom(this.api.refundOptions(this.id())).catch(
      (error: unknown) => {
        this.problem.set(toProblem(error));
        return null;
      },
    );

    if (!options) {
      return;
    }

    const done = await firstValueFrom(
      this.dialog
        .open<RefundDialogComponent, RefundDialogData, boolean>(RefundDialogComponent, {
          data: { paymentId: this.id(), options },
          autoFocus: 'dialog',
        })
        .afterClosed(),
    );

    if (done) {
      this.notify.success('paymentDetail.refundStarted');
      await this.load();
    }
  }

  async retry(refund: PaymentRefund): Promise<void> {
    const destination = this.retryDestination(refund);
    const result = await confirmAction(this.dialog, {
      title: this.translate.instant('paymentDetail.retryTitle') as string,
      explanation: this.translate.instant('paymentDetail.retryExplanation', {
        destination: this.translate.instant(`money.destination.${destination}`),
      }) as string,
      confirmLabel: this.translate.instant('paymentDetail.retry') as string,
      cancelLabel: this.translate.instant('common.cancel') as string,
      reasonLabel: this.translate.instant('paymentDetail.retryNote') as string,
    });

    if (!result) {
      return;
    }

    try {
      await firstValueFrom(
        this.api.retryRefund(
          refund.id,
          destination === refund.destination ? null : destination,
          result.reason || null,
        ),
      );
      this.notify.success('paymentDetail.retryStarted');
      this.retryTo.set(null);
      await this.load();
    } catch (error) {
      this.problem.set(toProblem(error));
    }
  }
}
