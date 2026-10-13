import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatBadgeModule } from '@angular/material/badge';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { Router } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MoneyApiService } from '../../core/api/money-api.service';
import { RefundRow, refundReasons, refundStatuses } from '../../core/api/step4.models';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { FilterBarComponent } from '../../shared/filter-bar.component';
import { DateTimePipe, MoneyPipe } from '../../shared/format.pipes';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { PagerComponent } from './money-shared';
import { HubEvents } from '../../core/realtime/hub-events';

const pageSize = 20;

/**
 * "Refund-lar" (plan section 8A.3): every refund, filtered by status and
 * reason (T-137), and the failed ones on a tab of their own with their count. A row opens its payment, where a failed
 * refund is retried or sent elsewhere.
 */
@Component({
  selector: 'app-refunds',
  imports: [
    FormsModule,
    MatBadgeModule,
    MatFormFieldModule,
    MatSelectModule,
    FilterBarComponent,
    MatButtonModule,
    MatTableModule,
    MatTabsModule,
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
        [title]="'refunds.title' | translate"
        [description]="'refunds.description' | translate"
      />

      <mat-tab-group [selectedIndex]="failedOnly() ? 1 : 0" (selectedIndexChange)="tab($event)">
        <mat-tab [label]="'refunds.tabAll' | translate" />
        <mat-tab>
          <ng-template mat-tab-label>
            <span [matBadge]="failedCount() || null" matBadgeOverlap="false" matBadgeColor="warn">{{
              'refunds.tabFailed' | translate
            }}</span>
          </ng-template>
        </mat-tab>
      </mat-tab-group>

      <app-filter-bar>
        @if (!failedOnly()) {
          <mat-form-field>
            <mat-label>{{ 'refunds.filterStatus' | translate }}</mat-label>
            <mat-select multiple [ngModel]="statuses()" (ngModelChange)="statuses.set($event); apply()">
              @for (option of statusOptions; track option) {
                <mat-option [value]="option">{{ 'money.refundStatus.' + option | translate }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        }
        <mat-form-field>
          <mat-label>{{ 'refunds.filterReason' | translate }}</mat-label>
          <mat-select multiple [ngModel]="reasons()" (ngModelChange)="reasons.set($event); apply()">
            @for (option of reasonOptions; track option) {
              <mat-option [value]="option">{{ 'money.refundReason.' + option | translate }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      </app-filter-bar>

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="refunds.loadFailedEffect" (retry)="load()" />
      }

      @if (loading()) {
        <div class="neo-card">
          @for (row of [1, 2, 3]; track row) {
            <div class="neo-skeleton neo-skeleton--row"></div>
          }
        </div>
      } @else if (rows().length === 0 && !problem()) {
        <app-empty-state
          [icon]="failedOnly() ? 'task_alt' : 'undo'"
          [message]="(failedOnly() ? 'refunds.emptyFailed' : 'refunds.empty') | translate"
          [hint]="(failedOnly() ? 'refunds.emptyFailedHint' : 'refunds.emptyHint') | translate"
        />
      } @else if (rows().length > 0) {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="rows()">
            <ng-container matColumnDef="created">
              <th mat-header-cell *matHeaderCellDef>{{ 'refunds.colCreated' | translate }}</th>
              <td mat-cell *matCellDef="let r">{{ r.createdAt | dateTime }}</td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'refunds.colStatus' | translate }}</th>
              <td mat-cell *matCellDef="let r">
                <app-status-chip
                  [status]="r.status"
                  [text]="'money.refundStatus.' + r.status | translate"
                />
              </td>
            </ng-container>
            <ng-container matColumnDef="reason">
              <th mat-header-cell *matHeaderCellDef>{{ 'refunds.colReason' | translate }}</th>
              <td mat-cell *matCellDef="let r">
                {{ 'money.refundReason.' + r.reason | translate }}
                <span class="neo-caption money__block">{{
                  'money.initiatedBy.' + r.initiatedBy | translate
                }}</span>
              </td>
            </ng-container>
            <ng-container matColumnDef="destination">
              <th mat-header-cell *matHeaderCellDef>{{ 'refunds.colDestination' | translate }}</th>
              <td mat-cell *matCellDef="let r">
                {{ 'money.destination.' + r.destination | translate }}
                @if (r.lastError && r.status === 'Failed') {
                  <span class="neo-caption money__block">{{ r.lastError }}</span>
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="amount">
              <th mat-header-cell *matHeaderCellDef class="money__number">
                {{ 'refunds.colAmount' | translate }}
              </th>
              <td mat-cell *matCellDef="let r" class="money__number">{{ r.amount | money }}</td>
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
          countKey="refunds.count"
          (go)="go($event)"
        />
      }
    </div>
  `,
  styleUrl: './money.scss',
})
export class RefundsComponent implements OnInit {
  private readonly api = inject(MoneyApiService);
  private readonly router = inject(Router);

  readonly pageSize = pageSize;
  readonly columns = ['created', 'status', 'reason', 'destination', 'amount'];
  readonly statusOptions = refundStatuses;
  readonly reasonOptions = refundReasons;

  readonly rows = signal<readonly RefundRow[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly failedOnly = signal(false);
  readonly statuses = signal<string[]>([]);
  readonly reasons = signal<string[]>([]);
  readonly failedCount = signal(0);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);

  constructor() {
    // A refund moves by itself: sent, confirmed by the bank, failed (T-248).
    liveRefresh({ events: [HubEvents.refundStatusChanged], reload: () => this.load(true) });
  }

  ngOnInit(): void {
    void this.load();
  }

  tab(index: number): void {
    this.failedOnly.set(index === 1);
    this.page.set(1);
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

  open(row: RefundRow): void {
    void this.router.navigate(['/payments', row.paymentId]);
  }

  /** Fetches the page; quietly when nobody asked - see `liveRefresh`. */
  async load(quiet = false): Promise<void> {
    if (!quiet) {
      this.loading.set(true);
      this.problem.set(null);
    }

    try {
      const [list, failed] = await Promise.all([
        firstValueFrom(
          this.api.listRefunds(
            this.failedOnly() ? ['Failed'] : this.statuses(),
            this.page(),
            pageSize,
            this.reasons(),
          ),
        ),
        firstValueFrom(this.api.listRefunds(['Failed'], 1, 1)),
      ]);
      this.rows.set(list.items);
      this.total.set(list.total);
      this.failedCount.set(failed.total);
    } catch (error) {
      if (!quiet) {
        this.problem.set(toProblem(error));
      }
    } finally {
      this.loading.set(false);
    }
  }
}
