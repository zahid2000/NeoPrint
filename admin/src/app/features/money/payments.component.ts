import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { ActivatedRoute, Router } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, KioskListItem } from '../../core/api/admin-api.service';
import { MoneyApiService } from '../../core/api/money-api.service';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { PaymentRow, paymentChannels, paymentMethods, paymentStatuses } from '../../core/api/step4.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { FilterBarComponent } from '../../shared/filter-bar.component';
import { provideNeoDates } from '../../shared/date-input';
import { DateTimePipe, MoneyPipe } from '../../shared/format.pipes';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { PagerComponent, endOfDay, startOfDay } from './money-shared';

const pageSize = 20;

/**
 * "Ödənişlər" (plan section 8A.3): every payment the admin may see, newest
 * first; filters for status, method, channel, kiosk, period, "xəta olub" and the
 * refund state. A row opens the payment.
 */
@Component({
  selector: 'app-payments',
  imports: [
    FormsModule,
    MatButtonModule,
    MatCheckboxModule,
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
    PagerComponent,
    DateTimePipe,
    MoneyPipe,
  ],
  providers: [provideNeoDates()],
  template: `
    <div class="neo-page">
      <app-page-header
        [title]="'payments.title' | translate"
        [description]="'payments.description' | translate"
      />

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="payments.loadFailedEffect" (retry)="load()" />
      }

      <app-filter-bar>
        <mat-form-field>
          <mat-label>{{ 'payments.filterSearch' | translate }}</mat-label>
          <mat-icon matPrefix>search</mat-icon>
          <input
            matInput
            [ngModel]="search()"
            (ngModelChange)="search.set($event)"
            (keyup.enter)="apply()"
            (blur)="apply()"
          />
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'payments.filterStatus' | translate }}</mat-label>
          <mat-select
            multiple
            [ngModel]="statuses()"
            (ngModelChange)="statuses.set($event); apply()"
          >
            @for (option of statusOptions; track option) {
              <mat-option [value]="option">{{ 'money.status.' + option | translate }}</mat-option>
            }
          </mat-select>
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'payments.filterMethod' | translate }}</mat-label>
          <mat-select multiple [ngModel]="methods()" (ngModelChange)="methods.set($event); apply()">
            @for (option of methodOptions; track option) {
              <mat-option [value]="option">{{ 'money.method.' + option | translate }}</mat-option>
            }
          </mat-select>
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'payments.filterChannel' | translate }}</mat-label>
          <mat-select [ngModel]="channel()" (ngModelChange)="channel.set($event); apply()">
            <mat-option [value]="null">{{ 'common.all' | translate }}</mat-option>
            @for (option of channelOptions; track option) {
              <mat-option [value]="option">{{ 'money.channel.' + option | translate }}</mat-option>
            }
          </mat-select>
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'payments.filterRefund' | translate }}</mat-label>
          <mat-select [ngModel]="refund()" (ngModelChange)="refund.set($event); apply()">
            <mat-option [value]="null">{{ 'common.all' | translate }}</mat-option>
            <mat-option value="pending">{{ 'payments.refundPending' | translate }}</mat-option>
            <mat-option value="failed">{{ 'payments.refundFailed' | translate }}</mat-option>
          </mat-select>
        </mat-form-field>

        @if (kiosks().length > 0) {
          <mat-form-field>
            <mat-label>{{ 'payments.filterKiosk' | translate }}</mat-label>
            <mat-select [ngModel]="kiosk()" (ngModelChange)="kiosk.set($event); apply()">
              <mat-option [value]="null">{{ 'payments.allKiosks' | translate }}</mat-option>
              @for (option of kiosks(); track option.id) {
                <mat-option [value]="option.id">{{ option.code }} — {{ option.name }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        }

        <mat-form-field class="money__period" subscriptSizing="dynamic">
          <mat-label>{{ 'payments.filterPeriod' | translate }}</mat-label>
          <mat-date-range-input [rangePicker]="period">
            <input
              matStartDate
              [placeholder]="'date.hint' | translate"
              [ngModel]="from()"
              (ngModelChange)="from.set(valid($event)); apply()"
            />
            <input
              matEndDate
              [placeholder]="'date.hint' | translate"
              [ngModel]="to()"
              (ngModelChange)="to.set(valid($event)); apply()"
            />
          </mat-date-range-input>
          <mat-datepicker-toggle matIconSuffix [for]="period" />
          <mat-date-range-picker #period />
          <mat-error>{{ 'date.invalid' | translate }}</mat-error>
        </mat-form-field>

        <mat-checkbox [ngModel]="hadError()" (ngModelChange)="hadError.set($event); apply()">
          {{ 'payments.filterHadError' | translate }}
        </mat-checkbox>
      </app-filter-bar>

      @if (loading()) {
        <div class="neo-card">
          @for (row of [1, 2, 3, 4]; track row) {
            <div class="neo-skeleton neo-skeleton--row"></div>
          }
        </div>
      } @else if (rows().length === 0 && !problem()) {
        @if (filtersActive()) {
          <app-empty-state icon="search_off" [message]="'payments.emptyFiltered' | translate">
            <button mat-stroked-button type="button" (click)="clear()">
              {{ 'payments.clearFilters' | translate }}
            </button>
          </app-empty-state>
        } @else {
          <app-empty-state
            icon="payments"
            [message]="'payments.empty' | translate"
            [hint]="'payments.emptyHint' | translate"
          />
        }
      } @else if (rows().length > 0) {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="rows()">
            <ng-container matColumnDef="created">
              <th mat-header-cell *matHeaderCellDef>{{ 'payments.colCreated' | translate }}</th>
              <td mat-cell *matCellDef="let p">{{ p.createdAt | dateTime }}</td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'payments.colStatus' | translate }}</th>
              <td mat-cell *matCellDef="let p">
                <app-status-chip
                  [status]="p.status"
                  [text]="'money.status.' + p.status | translate"
                />
              </td>
            </ng-container>
            <ng-container matColumnDef="method">
              <th mat-header-cell *matHeaderCellDef>{{ 'payments.colMethod' | translate }}</th>
              <td mat-cell *matCellDef="let p">
                {{ 'money.method.' + p.method | translate }}
                <span class="neo-caption money__block">{{
                  'money.purpose.' + p.purpose | translate
                }}</span>
              </td>
            </ng-container>
            <ng-container matColumnDef="kiosk">
              <th mat-header-cell *matHeaderCellDef>{{ 'payments.colKiosk' | translate }}</th>
              <td mat-cell *matCellDef="let p">
                {{ p.kioskCode ?? ('payments.noKiosk' | translate) }}
              </td>
            </ng-container>
            <ng-container matColumnDef="job">
              <th mat-header-cell *matHeaderCellDef>{{ 'payments.colJob' | translate }}</th>
              <td mat-cell *matCellDef="let p">{{ p.jobCode }}</td>
            </ng-container>
            <ng-container matColumnDef="amount">
              <th mat-header-cell *matHeaderCellDef class="money__number">
                {{ 'payments.colAmount' | translate }}
              </th>
              <td mat-cell *matCellDef="let p" class="money__number">
                {{ p.amount | money }}
                @if (p.refunded > 0) {
                  <span class="neo-caption money__block">{{
                    'payments.refundedAmount' | translate: { amount: (p.refunded | money) }
                  }}</span>
                }
              </td>
            </ng-container>

            <tr mat-header-row *matHeaderRowDef="columns"></tr>
            <tr
              mat-row
              class="neo-table__row--clickable"
              tabindex="0"
              *matRowDef="let row; columns: columns"
              (click)="open(row)"
              (keydown.enter)="open(row)"
            ></tr>
          </table>
        </div>

        <app-money-pager
          [total]="total()"
          [page]="page()"
          [pageSize]="pageSize"
          countKey="payments.count"
          (go)="go($event)"
        />
      }
    </div>
  `,
  styleUrl: './money.scss',
})
export class PaymentsComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly money = inject(MoneyApiService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly pageSize = pageSize;
  readonly statusOptions = paymentStatuses;
  readonly methodOptions = paymentMethods;
  readonly channelOptions = paymentChannels;
  readonly columns = ['created', 'status', 'method', 'kiosk', 'job', 'amount'];

  readonly rows = signal<readonly PaymentRow[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);
  readonly kiosks = signal<KioskListItem[]>([]);

  readonly search = signal('');
  readonly statuses = signal<string[]>([]);
  readonly methods = signal<string[]>([]);
  readonly channel = signal<string | null>(null);
  readonly refund = signal<string | null>(null);
  readonly kiosk = signal<string | null>(null);
  readonly from = signal<Date | null>(null);
  readonly to = signal<Date | null>(null);
  readonly hadError = signal(false);

  readonly filtersActive = computed(
    () =>
      this.search().trim() !== '' ||
      this.statuses().length > 0 ||
      this.methods().length > 0 ||
      this.channel() !== null ||
      this.refund() !== null ||
      this.kiosk() !== null ||
      this.from() !== null ||
      this.to() !== null ||
      this.hadError(),
  );

  private lastQuery = '';

  constructor() {
    // A payment arrives and a refund moves while the list is open (T-248).
    liveRefresh({
      events: ['PaymentSucceeded', 'RefundStatusChanged'],
      reload: () => this.load(true),
    });
  }

  ngOnInit(): void {
    // A link from the dashboard ("uğursuz refund-lar") arrives with its filter set.
    const refund = this.route.snapshot.queryParamMap.get('refund');
    if (refund === 'failed' || refund === 'pending') {
      this.refund.set(refund);
    }

    if (this.auth.can(Permissions.kiosksView)) {
      firstValueFrom(this.api.listKiosks())
        .then((kiosks) => this.kiosks.set(kiosks))
        .catch(() => this.kiosks.set([]));
    }

    void this.load();
  }

  apply(): void {
    this.page.set(1);
    void this.load();
  }

  go(page: number): void {
    this.page.set(page);
    void this.load();
  }

  clear(): void {
    this.search.set('');
    this.statuses.set([]);
    this.methods.set([]);
    this.channel.set(null);
    this.refund.set(null);
    this.kiosk.set(null);
    this.from.set(null);
    this.to.set(null);
    this.hadError.set(false);
    this.apply();
  }

  valid(value: Date | null): Date | null {
    return value && !Number.isNaN(value.getTime()) ? value : null;
  }

  open(row: PaymentRow): void {
    void this.router.navigate(['/payments', row.id]);
  }

  /** Fetches the page; quietly when nobody asked - see `liveRefresh`. */
  async load(quiet = false): Promise<void> {
    const filter = {
      search: this.search(),
      statuses: this.statuses(),
      methods: this.methods(),
      channel: this.channel(),
      refund: this.refund(),
      kioskId: this.kiosk(),
      from: startOfDay(this.from()),
      to: endOfDay(this.to()),
      hadError: this.hadError(),
      page: this.page(),
      pageSize,
    };

    const query = JSON.stringify(filter);
    if (!quiet && query === this.lastQuery && !this.problem() && !this.loading()) {
      return;
    }

    this.lastQuery = query;

    if (!quiet) {
      this.loading.set(true);
      this.problem.set(null);
    }

    try {
      const result = await firstValueFrom(this.money.listPayments(filter));
      this.rows.set(result.items);
      this.total.set(result.total);
    } catch (error) {
      if (!quiet) {
        this.problem.set(toProblem(error));
      }
    } finally {
      this.loading.set(false);
    }
  }
}
