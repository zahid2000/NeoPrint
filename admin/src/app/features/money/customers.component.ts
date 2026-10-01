import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { Router } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MoneyApiService } from '../../core/api/money-api.service';
import { CustomerRow } from '../../core/api/step4.models';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { FilterBarComponent } from '../../shared/filter-bar.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { PagerComponent } from './money-shared';

const pageSize = 20;

/**
 * "İstifadəçilər" (plan section 8A.3): the app's customers by email or
 * public id. A row opens the customer: wallet, jobs, consents, blocking.
 */
@Component({
  selector: 'app-customers',
  imports: [
    FormsModule,
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
  ],
  template: `
    <div class="neo-page">
      <app-page-header
        [title]="'customers.title' | translate"
        [description]="'customers.description' | translate"
      />

      @if (problem(); as problem) {
        <app-error-banner
          [problem]="problem"
          effect="customers.loadFailedEffect"
          (retry)="load()"
        />
      }

      <app-filter-bar>
        <mat-form-field>
          <mat-label>{{ 'customers.filterSearch' | translate }}</mat-label>
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
          <mat-label>{{ 'customers.filterStatus' | translate }}</mat-label>
          <mat-select [ngModel]="status()" (ngModelChange)="status.set($event); apply(true)">
            <mat-option [value]="null">{{ 'common.all' | translate }}</mat-option>
            <mat-option value="Active">{{ 'money.customerStatus.Active' | translate }}</mat-option>
            <mat-option value="Blocked">{{
              'money.customerStatus.Blocked' | translate
            }}</mat-option>
          </mat-select>
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
          icon="person_search"
          [message]="'customers.empty' | translate"
          [hint]="'customers.emptyHint' | translate"
        />
      } @else if (rows().length > 0) {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="rows()">
            <ng-container matColumnDef="email">
              <th mat-header-cell *matHeaderCellDef>{{ 'customers.colEmail' | translate }}</th>
              <td mat-cell *matCellDef="let c">
                {{ c.email }}
                @if (c.fullName) {
                  <span class="neo-caption money__block">{{ c.fullName }}</span>
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="publicId">
              <th mat-header-cell *matHeaderCellDef>{{ 'customers.colPublicId' | translate }}</th>
              <td mat-cell *matCellDef="let c">{{ c.publicId }}</td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'customers.colStatus' | translate }}</th>
              <td mat-cell *matCellDef="let c">
                <app-status-chip
                  [status]="c.status"
                  [text]="'money.customerStatus.' + c.status | translate"
                />
              </td>
            </ng-container>
            <ng-container matColumnDef="created">
              <th mat-header-cell *matHeaderCellDef>{{ 'customers.colCreated' | translate }}</th>
              <td mat-cell *matCellDef="let c">{{ c.createdAt | dateTime }}</td>
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
          countKey="customers.count"
          (go)="go($event)"
        />
      }
    </div>
  `,
  styleUrl: './money.scss',
})
export class CustomersComponent implements OnInit {
  private readonly api = inject(MoneyApiService);
  private readonly router = inject(Router);

  readonly pageSize = pageSize;
  readonly columns = ['email', 'publicId', 'status', 'created'];

  readonly rows = signal<readonly CustomerRow[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly search = signal('');
  readonly status = signal<string | null>(null);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);

  private lastSearch = '';

  ngOnInit(): void {
    void this.load();
  }

  apply(force = false): void {
    if (!force && this.search().trim() === this.lastSearch) {
      return;
    }

    this.page.set(1);
    void this.load();
  }

  go(page: number): void {
    this.page.set(page);
    void this.load();
  }

  open(row: CustomerRow): void {
    void this.router.navigate(['/customers', row.id]);
  }

  async load(): Promise<void> {
    this.lastSearch = this.search().trim();
    this.loading.set(true);
    this.problem.set(null);

    try {
      const result = await firstValueFrom(
        this.api.listCustomers(this.search(), this.status(), this.page(), pageSize),
      );
      this.rows.set(result.items);
      this.total.set(result.total);
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }
}
