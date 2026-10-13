import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MoneyApiService } from '../../core/api/money-api.service';
import { ReceiptRow } from '../../core/api/step4.models';
import { HubEvents } from '../../core/realtime/hub-events';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { FilterBarComponent } from '../../shared/filter-bar.component';
import { DateTimePipe, MoneyPipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { PagerComponent } from './money-shared';

const pageSize = 20;

/**
 * "Qəbzlər" (plan section 8A.3): NeoPrint's own receipts by number; the PDF
 * opens in a new tab, and a receipt can be sent again to where it went before.
 */
@Component({
  selector: 'app-receipts',
  imports: [
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatTableModule,
    RouterLink,
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
  template: `
    <div class="neo-page">
      <app-page-header
        [title]="'receipts.title' | translate"
        [description]="'receipts.description' | translate"
      />

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="receipts.loadFailedEffect" (retry)="load()" />
      }

      <app-filter-bar>
        <mat-form-field>
          <mat-label>{{ 'receipts.filterNumber' | translate }}</mat-label>
          <mat-icon matPrefix>search</mat-icon>
          <input
            matInput
            placeholder="NP-2026-000001"
            [ngModel]="number()"
            (ngModelChange)="number.set($event)"
            (keyup.enter)="apply()"
            (blur)="apply()"
          />
        </mat-form-field>
      </app-filter-bar>

      @if (loading()) {
        <div class="neo-card">
          @for (row of [1, 2, 3]; track row) {
            <div class="neo-skeleton neo-skeleton--row"></div>
          }
        </div>
      } @else if (rows().length === 0 && !problem()) {
        <app-empty-state
          icon="receipt_long"
          [message]="'receipts.empty' | translate"
          [hint]="'receipts.emptyHint' | translate"
        />
      } @else if (rows().length > 0) {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="rows()">
            <ng-container matColumnDef="number">
              <th mat-header-cell *matHeaderCellDef>{{ 'receipts.colNumber' | translate }}</th>
              <td mat-cell *matCellDef="let r">
                <strong>{{ r.number }}</strong>
                <a class="neo-caption money__block" [routerLink]="['/payments', r.paymentId]">{{
                  'receipts.openPayment' | translate
                }}</a>
              </td>
            </ng-container>
            <ng-container matColumnDef="created">
              <th mat-header-cell *matHeaderCellDef>{{ 'receipts.colCreated' | translate }}</th>
              <td mat-cell *matCellDef="let r">
                {{ r.createdAt | dateTime }}
                <span class="neo-caption money__block">{{
                  'money.purpose.' + r.purpose | translate
                }}</span>
              </td>
            </ng-container>
            <ng-container matColumnDef="fiscal">
              <th mat-header-cell *matHeaderCellDef>{{ 'receipts.colFiscal' | translate }}</th>
              <td mat-cell *matCellDef="let r">
                @if (r.fiscalStatus !== 'None') {
                  <app-status-chip
                    [status]="r.fiscalStatus"
                    [text]="'money.fiscalStatus.' + r.fiscalStatus | translate"
                  />
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="amount">
              <th mat-header-cell *matHeaderCellDef class="money__number">
                {{ 'receipts.colAmount' | translate }}
              </th>
              <td mat-cell *matCellDef="let r" class="money__number">{{ r.amount | money }}</td>
            </ng-container>
            <ng-container matColumnDef="actions">
              <th mat-header-cell *matHeaderCellDef></th>
              <td mat-cell *matCellDef="let r">
                <button mat-button type="button" (click)="pdf(r)">
                  <mat-icon>picture_as_pdf</mat-icon>{{ 'receipts.pdf' | translate }}
                </button>
                @if (r.emailedCount > 0) {
                  <button mat-button type="button" [disabled]="busy() === r.id" (click)="resend(r)">
                    <mat-icon>forward_to_inbox</mat-icon>{{ 'receipts.resend' | translate }}
                  </button>
                }
              </td>
            </ng-container>

            <tr mat-header-row *matHeaderRowDef="columns"></tr>
            <tr mat-row *matRowDef="let row; columns: columns"></tr>
          </table>
        </div>
        <app-money-pager
          [total]="total()"
          [page]="page()"
          [pageSize]="pageSize"
          countKey="receipts.count"
          (go)="go($event)"
        />
      }
    </div>
  `,
  styleUrl: './money.scss',
})
export class ReceiptsComponent implements OnInit {
  private readonly api = inject(MoneyApiService);
  private readonly notify = inject(NotifyService);

  readonly pageSize = pageSize;
  readonly columns = ['number', 'created', 'fiscal', 'amount', 'actions'];

  readonly rows = signal<readonly ReceiptRow[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly number = signal('');
  readonly loading = signal(true);
  readonly busy = signal<string | null>(null);
  readonly problem = signal<Problem | null>(null);

  private lastQuery = '';

  constructor() {
    // A receipt's fiscal status moves by itself: pending, issued, failed (T-253).
    liveRefresh({ events: [HubEvents.fiscalStatusChanged], reload: () => this.load(true) });
  }

  ngOnInit(): void {
    void this.load();
  }

  apply(): void {
    if (this.number().trim() === this.lastQuery) {
      return;
    }

    this.page.set(1);
    void this.load();
  }

  go(page: number): void {
    this.page.set(page);
    void this.load();
  }

  /** A quiet fetch is one nobody asked for: the table stays, and one that fails says nothing. */
  async load(quiet = false): Promise<void> {
    this.lastQuery = this.number().trim();

    if (!quiet) {
      this.loading.set(true);
      this.problem.set(null);
    }

    try {
      const result = await firstValueFrom(
        this.api.listReceipts(this.number(), this.page(), pageSize),
      );
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

  /** Opens the PDF in a new tab; the blob URL is let go once the tab has it. */
  async pdf(row: ReceiptRow): Promise<void> {
    try {
      const blob = await firstValueFrom(this.api.receiptPdf(row.id));
      const url = URL.createObjectURL(blob);
      window.open(url, '_blank', 'noopener');
      setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (error) {
      this.problem.set(toProblem(error));
    }
  }

  async resend(row: ReceiptRow): Promise<void> {
    this.busy.set(row.id);

    try {
      await firstValueFrom(this.api.resendReceipt(row.id));
      this.notify.success('receipts.resent');
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.busy.set(null);
    }
  }
}
