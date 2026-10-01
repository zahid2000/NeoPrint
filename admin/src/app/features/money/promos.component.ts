import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatTableModule } from '@angular/material/table';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, KioskListItem } from '../../core/api/admin-api.service';
import { MoneyApiService } from '../../core/api/money-api.service';
import { PricingProfile } from '../../core/api/step2.models';
import { Promo } from '../../core/api/step4.models';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { FilterBarComponent } from '../../shared/filter-bar.component';
import { DateTimePipe, MoneyPipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { PagerComponent } from './money-shared';
import { PromoDialogComponent, PromoDialogData } from './promo-dialog.component';

const pageSize = 20;

/**
 * "Promokodlar" (plan section 8A.3): the codes, how much of each was used and
 * what it gave away; created, edited, switched on and off - never deleted,
 * because a used code is part of receipts and payments.
 */
@Component({
  selector: 'app-promos',
  imports: [
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
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
  template: `
    <div class="neo-page">
      <div class="money__head">
        <app-page-header
          [title]="'promos.title' | translate"
          [description]="'promos.description' | translate"
        />
        <button mat-flat-button type="button" (click)="edit(null)">
          <mat-icon>add</mat-icon>{{ 'promos.new' | translate }}
        </button>
      </div>

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="promos.loadFailedEffect" (retry)="load()" />
      }

      <app-filter-bar>
        <mat-form-field>
          <mat-label>{{ 'promos.filterCode' | translate }}</mat-label>
          <mat-icon matPrefix>search</mat-icon>
          <input
            matInput
            [ngModel]="code()"
            (ngModelChange)="code.set($event)"
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
          icon="sell"
          [message]="'promos.empty' | translate"
          [hint]="'promos.emptyHint' | translate"
        />
      } @else if (rows().length > 0) {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="rows()">
            <ng-container matColumnDef="code">
              <th mat-header-cell *matHeaderCellDef>{{ 'promos.colCode' | translate }}</th>
              <td mat-cell *matCellDef="let p">
                <strong>{{ p.code }}</strong>
                @if (p.description) {
                  <span class="neo-caption money__block">{{ p.description }}</span>
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="value">
              <th mat-header-cell *matHeaderCellDef>{{ 'promos.colValue' | translate }}</th>
              <td mat-cell *matCellDef="let p">
                @if (p.kind === 'Percent') {
                  {{ p.value }}%
                } @else {
                  {{ p.value | money }}
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="period">
              <th mat-header-cell *matHeaderCellDef>{{ 'promos.colPeriod' | translate }}</th>
              <td mat-cell *matCellDef="let p">
                @if (p.startsAt || p.endsAt) {
                  {{ p.startsAt ? (p.startsAt | dateTime) : '…' }} –
                  {{ p.endsAt ? (p.endsAt | dateTime) : '…' }}
                } @else {
                  {{ 'promos.always' | translate }}
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="used">
              <th mat-header-cell *matHeaderCellDef class="money__number">
                {{ 'promos.colUsed' | translate }}
              </th>
              <td mat-cell *matCellDef="let p" class="money__number">
                {{ p.confirmedCount }}{{ p.totalLimit ? ' / ' + p.totalLimit : '' }}
                <span class="neo-caption money__block">{{
                  'promos.given' | translate: { amount: (p.totalDiscount | money) }
                }}</span>
              </td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'promos.colStatus' | translate }}</th>
              <td mat-cell *matCellDef="let p">
                <app-status-chip
                  [status]="p.isActive ? 'Active' : 'Inactive'"
                  [text]="(p.isActive ? 'promos.active' : 'promos.inactive') | translate"
                />
              </td>
            </ng-container>
            <ng-container matColumnDef="actions">
              <th mat-header-cell *matHeaderCellDef></th>
              <td mat-cell *matCellDef="let p">
                <button mat-button type="button" (click)="edit(p)">
                  {{ 'common.edit' | translate }}
                </button>
                <button mat-button type="button" (click)="toggle(p)">
                  {{ (p.isActive ? 'promos.deactivate' : 'promos.activate') | translate }}
                </button>
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
          countKey="promos.count"
          (go)="go($event)"
        />
      }
    </div>
  `,
  styleUrl: './money.scss',
})
export class PromosComponent implements OnInit {
  private readonly api = inject(MoneyApiService);
  private readonly admin = inject(AdminApiService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);

  readonly pageSize = pageSize;
  readonly columns = ['code', 'value', 'period', 'used', 'status', 'actions'];

  readonly rows = signal<readonly Promo[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly code = signal('');
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);

  private kiosks: readonly KioskListItem[] = [];
  private profiles: readonly PricingProfile[] = [];
  private lastCode = '';

  ngOnInit(): void {
    // The restriction pickers: without them a code still applies everywhere.
    firstValueFrom(this.admin.listKiosks())
      .then((k) => (this.kiosks = k))
      .catch(() => undefined);
    firstValueFrom(this.admin.listPricingProfiles())
      .then((p) => (this.profiles = p))
      .catch(() => undefined);
    void this.load();
  }

  apply(): void {
    if (this.code().trim() === this.lastCode) {
      return;
    }

    this.page.set(1);
    void this.load();
  }

  go(page: number): void {
    this.page.set(page);
    void this.load();
  }

  async load(): Promise<void> {
    this.lastCode = this.code().trim();
    this.loading.set(true);
    this.problem.set(null);

    try {
      const result = await firstValueFrom(this.api.listPromos(this.code(), this.page(), pageSize));
      this.rows.set(result.items);
      this.total.set(result.total);
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }

  async edit(promo: Promo | null): Promise<void> {
    const saved = await firstValueFrom(
      this.dialog
        .open<PromoDialogComponent, PromoDialogData, boolean>(PromoDialogComponent, {
          data: { promo, kiosks: this.kiosks, profiles: this.profiles },
          autoFocus: 'dialog',
        })
        .afterClosed(),
    );

    if (saved) {
      this.notify.success('promos.saved');
      await this.load();
    }
  }

  async toggle(promo: Promo): Promise<void> {
    try {
      await firstValueFrom(this.api.setPromoActive(promo.id, !promo.isActive));
      await this.load();
    } catch (error) {
      this.problem.set(toProblem(error));
    }
  }
}
