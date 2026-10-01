import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogModule,
  MatDialogRef,
} from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { RouterLink } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MoneyApiService } from '../../core/api/money-api.service';
import { CustomerDetail, WalletEntry } from '../../core/api/step4.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { confirmAction } from '../../shared/confirm.dialog';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { BytesPipe, DateTimePipe, MoneyPipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';

/**
 * One customer (plan section 8A.3, "İstifadəçilər"): profile, wallet and its
 * ledger, recent jobs, file totals (never contents), referrals and consents;
 * a balance correction with a reason, and block / unblock.
 */
@Component({
  selector: 'app-customer-detail',
  imports: [
    MatButtonModule,
    MatIconModule,
    RouterLink,
    TranslatePipe,
    PageHeaderComponent,
    ErrorBannerComponent,
    StatusChipComponent,
    BytesPipe,
    DateTimePipe,
    MoneyPipe,
  ],
  template: `
    <div class="neo-page">
      <app-page-header
        [title]="'customerDetail.title' | translate"
        [description]="'customerDetail.description' | translate"
      />
      <a mat-button routerLink="/customers"
        ><mat-icon>arrow_back</mat-icon>{{ 'customerDetail.back' | translate }}</a
      >

      @if (problem(); as problem) {
        <app-error-banner
          [problem]="problem"
          effect="customerDetail.loadFailedEffect"
          (retry)="load()"
        />
      }

      @if (detail(); as d) {
        <div class="money__head">
          <div class="money__headline">
            <p class="money__amount">{{ d.profile.email }}</p>
            <app-status-chip
              [status]="d.profile.status"
              [text]="'money.customerStatus.' + d.profile.status | translate"
            />
          </div>
          @if (canBlock()) {
            @if (d.profile.status === 'Blocked') {
              <button mat-stroked-button type="button" (click)="setBlocked(false)">
                {{ 'customerDetail.unblock' | translate }}
              </button>
            } @else {
              <button mat-stroked-button type="button" (click)="setBlocked(true)">
                <mat-icon>block</mat-icon>{{ 'customerDetail.block' | translate }}
              </button>
            }
          }
        </div>

        <div class="money__grid">
          <section class="neo-card">
            <h2 class="money__card-title">{{ 'customerDetail.profile' | translate }}</h2>
            <dl class="money__rows">
              <dt>{{ 'customerDetail.name' | translate }}</dt>
              <dd>{{ d.profile.fullName ?? ('customerDetail.noName' | translate) }}</dd>
              <dt>{{ 'customerDetail.publicId' | translate }}</dt>
              <dd>{{ d.profile.publicId }}</dd>
              <dt>{{ 'customerDetail.language' | translate }}</dt>
              <dd>{{ d.language }}</dd>
              <dt>{{ 'customerDetail.created' | translate }}</dt>
              <dd>{{ d.profile.createdAt | dateTime }}</dd>
              @if (d.profile.blockedAt) {
                <dt>{{ 'customerDetail.blockedAt' | translate }}</dt>
                <dd>{{ d.profile.blockedAt | dateTime }}</dd>
              }
              <dt>{{ 'customerDetail.files' | translate }}</dt>
              <dd>
                {{
                  'customerDetail.filesValue'
                    | translate: { count: d.fileCount, size: (d.fileBytes | bytes) }
                }}
              </dd>
              <dt>{{ 'customerDetail.referred' | translate }}</dt>
              <dd>{{ d.referred }}</dd>
            </dl>
            <p class="neo-caption">
              {{ 'customerDetail.consents' | translate }}:
              @for (c of d.consents; track c.docType) {
                {{ 'customerDetail.consent.' + c.docType | translate }} {{ c.version }}
                @if (!$last) {
                  ,
                }
              }
            </p>
          </section>

          <section class="neo-card">
            <h2 class="money__card-title">{{ 'customerDetail.wallet' | translate }}</h2>
            <p class="money__amount">{{ d.wallet.balance | money }}</p>
            @if (d.wallet.bonusPages > 0) {
              <p class="neo-caption">
                {{ 'customerDetail.bonusPages' | translate: { count: d.wallet.bonusPages } }}
              </p>
            }
            @if (canAdjust()) {
              <div class="money__actions">
                <button mat-stroked-button type="button" (click)="adjust()">
                  {{ 'customerDetail.adjust' | translate }}
                </button>
              </div>
            }
            <h3 class="neo-caption">{{ 'customerDetail.ledger' | translate }}</h3>
            @for (e of entries(); track e.id) {
              <div class="money__item">
                <div class="money__item-head">
                  <span>{{ 'money.walletType.' + e.type | translate }}</span>
                  <strong class="money__number"
                    >{{ e.amount > 0 ? '+' : '' }}{{ e.amount | money }}</strong
                  >
                </div>
                <span class="neo-caption"
                  >{{ e.createdAt | dateTime }} ·
                  {{
                    'customerDetail.after' | translate: { amount: (e.balanceAfter | money) }
                  }}</span
                >
                @if (e.note) {
                  <span class="neo-caption money__block">{{ e.note }}</span>
                }
              </div>
            } @empty {
              <p class="neo-muted">{{ 'customerDetail.noLedger' | translate }}</p>
            }
          </section>
        </div>

        <section class="neo-card">
          <h2 class="money__card-title">{{ 'customerDetail.jobs' | translate }}</h2>
          @for (j of d.recentJobs; track j.id) {
            <div class="money__item">
              <div class="money__item-head">
                <a [routerLink]="['/print-jobs', j.id]">{{ j.jobCode }}</a>
                <span>{{ j.fileName ?? ('jobs.noFile' | translate) }} · {{ j.kioskCode }}</span>
                <app-status-chip
                  [status]="j.status"
                  [text]="'jobs.status.' + j.status | translate"
                />
                <strong class="money__number">{{ j.amount | money }}</strong>
              </div>
              <span class="neo-caption">{{ j.createdAt | dateTime }}</span>
            </div>
          } @empty {
            <p class="neo-muted">{{ 'customerDetail.noJobs' | translate }}</p>
          }
        </section>
      }
    </div>
  `,
  styleUrl: './money.scss',
})
export class CustomerDetailComponent implements OnInit {
  private readonly api = inject(MoneyApiService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly translate = inject(TranslateService);

  readonly id = input.required<string>();

  readonly detail = signal<CustomerDetail | null>(null);
  readonly entries = signal<readonly WalletEntry[]>([]);
  readonly problem = signal<Problem | null>(null);

  readonly canBlock = computed(() => this.auth.can(Permissions.usersBlock));
  readonly canAdjust = computed(() => this.auth.can(Permissions.walletsAdjust));

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.problem.set(null);

    try {
      const [detail, ledger] = await Promise.all([
        firstValueFrom(this.api.getCustomer(this.id())),
        firstValueFrom(this.api.walletEntries(this.id(), 1, 20)),
      ]);
      this.detail.set(detail);
      this.entries.set(ledger.items);
    } catch (error) {
      this.problem.set(toProblem(error));
    }
  }

  async setBlocked(blocked: boolean): Promise<void> {
    const t = (key: string): string => this.translate.instant(key) as string;
    const result = await confirmAction(this.dialog, {
      title: t(blocked ? 'customerDetail.blockTitle' : 'customerDetail.unblockTitle'),
      explanation: t(
        blocked ? 'customerDetail.blockExplanation' : 'customerDetail.unblockExplanation',
      ),
      confirmLabel: t(blocked ? 'customerDetail.block' : 'customerDetail.unblock'),
      cancelLabel: t('common.cancel'),
      destructive: blocked,
      requireReason: true,
      reasonLabel: t('common.reason'),
    });

    if (!result?.reason) {
      return;
    }

    try {
      await firstValueFrom(this.api.setCustomerBlocked(this.id(), blocked, result.reason));
      this.notify.success(blocked ? 'customerDetail.blocked' : 'customerDetail.unblocked');
      await this.load();
    } catch (error) {
      this.problem.set(toProblem(error));
    }
  }

  async adjust(): Promise<void> {
    const done = await firstValueFrom(
      this.dialog
        .open<
          AdjustWalletDialogComponent,
          string,
          boolean
        >(AdjustWalletDialogComponent, { data: this.id(), autoFocus: 'dialog' })
        .afterClosed(),
    );

    if (done) {
      this.notify.success('customerDetail.adjusted');
      await this.load();
    }
  }
}

/** A balance correction (plan section 2.14): signed amount, bonus pages, a mandatory reason; never below zero. */
@Component({
  selector: 'app-adjust-wallet-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    TranslatePipe,
    ErrorBannerComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ 'customerDetail.adjustTitle' | translate }}</h2>
    <mat-dialog-content class="neo-dialog">
      <p class="neo-dialog__explanation">{{ 'customerDetail.adjustExplanation' | translate }}</p>
      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="customerDetail.adjustFailedEffect" />
      }
      <div class="money__form">
        <mat-form-field>
          <mat-label>{{ 'customerDetail.adjustAmount' | translate }}</mat-label>
          <input
            matInput
            type="number"
            step="0.01"
            [ngModel]="amount()"
            (ngModelChange)="amount.set($event)"
          />
          <mat-hint>{{ 'customerDetail.adjustAmountHint' | translate }}</mat-hint>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'customerDetail.adjustPages' | translate }}</mat-label>
          <input
            matInput
            type="number"
            step="1"
            [ngModel]="pages()"
            (ngModelChange)="pages.set($event)"
          />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'common.reason' | translate }}</mat-label>
          <textarea
            matInput
            rows="3"
            maxlength="500"
            [ngModel]="reason()"
            (ngModelChange)="reason.set($event)"
          ></textarea>
        </mat-form-field>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions class="neo-dialog__actions">
      <button mat-button type="button" [disabled]="busy()" (click)="ref.close(false)">
        {{ 'common.cancel' | translate }}
      </button>
      <button mat-flat-button type="button" [disabled]="!valid() || busy()" (click)="save()">
        {{ 'common.save' | translate }}
      </button>
    </mat-dialog-actions>
  `,
  styleUrl: './money.scss',
})
export class AdjustWalletDialogComponent {
  readonly userId = inject<string>(MAT_DIALOG_DATA);
  readonly ref = inject<MatDialogRef<AdjustWalletDialogComponent, boolean>>(MatDialogRef);
  private readonly api = inject(MoneyApiService);

  readonly amount = signal<number>(0);
  readonly pages = signal<number>(0);
  readonly reason = signal('');
  readonly busy = signal(false);
  readonly problem = signal<Problem | null>(null);

  readonly valid = computed(
    () =>
      this.reason().trim().length > 0 &&
      (Number(this.amount() || 0) !== 0 || Number(this.pages() || 0) !== 0),
  );

  async save(): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);

    try {
      await firstValueFrom(
        this.api.adjustWallet(
          this.userId,
          Math.round(Number(this.amount() || 0) * 100) / 100,
          Math.trunc(Number(this.pages() || 0)),
          this.reason().trim(),
        ),
      );
      this.ref.close(true);
    } catch (error) {
      this.problem.set(toProblem(error));
      this.busy.set(false);
    }
  }
}
