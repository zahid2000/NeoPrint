import { Component, OnInit, inject, signal } from '@angular/core';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MoneyApiService } from '../../core/api/money-api.service';
import { VoucherRow } from '../../core/api/step4.models';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe, MoneyPipe } from '../../shared/format.pipes';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { PagerComponent } from './money-shared';

const pageSize = 20;

/**
 * "Voucher-lər" (plan section 8A.3): codes given instead of a bank refund an
 * anonymous customer could not get, and whether they were used.
 */
@Component({
  selector: 'app-vouchers',
  imports: [
    MatTableModule,
    RouterLink,
    TranslatePipe,
    PageHeaderComponent,
    ErrorBannerComponent,
    EmptyStateComponent,
    StatusChipComponent,
    PagerComponent,
    DateTimePipe,
    MoneyPipe,
  ],
  template: `
    <div class="neo-page">
      <app-page-header
        [title]="'vouchers.title' | translate"
        [description]="'vouchers.description' | translate"
      />

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="vouchers.loadFailedEffect" (retry)="load()" />
      }

      @if (loading()) {
        <div class="neo-card">
          @for (row of [1, 2, 3]; track row) {
            <div class="neo-skeleton neo-skeleton--row"></div>
          }
        </div>
      } @else if (rows().length === 0 && !problem()) {
        <app-empty-state
          icon="confirmation_number"
          [message]="'vouchers.empty' | translate"
          [hint]="'vouchers.emptyHint' | translate"
        />
      } @else if (rows().length > 0) {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="rows()">
            <ng-container matColumnDef="code">
              <th mat-header-cell *matHeaderCellDef>{{ 'vouchers.colCode' | translate }}</th>
              <td mat-cell *matCellDef="let v">
                <strong>{{ v.code }}</strong>
                <a class="neo-caption money__block" [routerLink]="['/payments', v.paymentId]">{{
                  'vouchers.openPayment' | translate
                }}</a>
              </td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'vouchers.colStatus' | translate }}</th>
              <td mat-cell *matCellDef="let v">
                <app-status-chip
                  [status]="v.status"
                  [text]="'money.voucherStatus.' + v.status | translate"
                />
              </td>
            </ng-container>
            <ng-container matColumnDef="dates">
              <th mat-header-cell *matHeaderCellDef>{{ 'vouchers.colDates' | translate }}</th>
              <td mat-cell *matCellDef="let v">
                {{ v.createdAt | dateTime }}
                <span class="neo-caption money__block">
                  @if (v.redeemedAt) {
                    {{ 'vouchers.redeemedAt' | translate: { time: (v.redeemedAt | dateTime) } }}
                  } @else {
                    {{ 'vouchers.expiresAt' | translate: { time: (v.expiresAt | dateTime) } }}
                  }
                </span>
              </td>
            </ng-container>
            <ng-container matColumnDef="amount">
              <th mat-header-cell *matHeaderCellDef class="money__number">
                {{ 'vouchers.colAmount' | translate }}
              </th>
              <td mat-cell *matCellDef="let v" class="money__number">{{ v.amount | money }}</td>
            </ng-container>

            <tr mat-header-row *matHeaderRowDef="columns"></tr>
            <tr mat-row *matRowDef="let row; columns: columns"></tr>
          </table>
        </div>
        <app-money-pager
          [total]="total()"
          [page]="page()"
          [pageSize]="pageSize"
          countKey="vouchers.count"
          (go)="go($event)"
        />
      }
    </div>
  `,
  styleUrl: './money.scss',
})
export class VouchersComponent implements OnInit {
  private readonly api = inject(MoneyApiService);

  readonly pageSize = pageSize;
  readonly columns = ['code', 'status', 'dates', 'amount'];

  readonly rows = signal<readonly VoucherRow[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);

  ngOnInit(): void {
    void this.load();
  }

  go(page: number): void {
    this.page.set(page);
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.problem.set(null);

    try {
      const result = await firstValueFrom(this.api.listVouchers(this.page(), pageSize));
      this.rows.set(result.items);
      this.total.set(result.total);
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }
}
