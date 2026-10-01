import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { PlatformSetting } from '../../core/api/admin-api.service';
import { MoneyApiService } from '../../core/api/money-api.service';
import { FiscalRow } from '../../core/api/step4.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
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
 * "Fiskal çeklər" (plan section 8A.3): every fiscal receipt, filtered by
 * status and type; a failed one is retried from here. The fiscal settings
 * (online terminal, top-up mode) sit above the list for whoever may change them.
 */
@Component({
  selector: 'app-fiscal',
  imports: [
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
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
        [title]="'fiscal.title' | translate"
        [description]="'fiscal.description' | translate"
      />

      @if (canManage() && settings().length > 0) {
        <section class="neo-card settings">
          <h2 class="money__card-title">{{ 'fiscal.settings' | translate }}</h2>
          <div class="money__form">
            @for (s of settings(); track s.key) {
              <mat-form-field>
                <mat-label>{{ 'fiscal.setting.' + s.key | translate }}</mat-label>
                <input
                  matInput
                  [ngModel]="values()[s.key]"
                  (ngModelChange)="setValue(s.key, $event)"
                />
                <mat-hint>{{ s.key }}</mat-hint>
              </mat-form-field>
            }
          </div>
          <div class="money__actions">
            <button
              mat-flat-button
              type="button"
              [disabled]="!dirty() || saving()"
              (click)="save()"
            >
              {{ 'common.save' | translate }}
            </button>
          </div>
        </section>
      }

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="fiscal.loadFailedEffect" (retry)="load()" />
      }

      <app-filter-bar>
        <mat-form-field>
          <mat-label>{{ 'fiscal.filterStatus' | translate }}</mat-label>
          <mat-select [ngModel]="status()" (ngModelChange)="status.set($event); apply()">
            <mat-option [value]="null">{{ 'common.all' | translate }}</mat-option>
            @for (option of statuses; track option) {
              <mat-option [value]="option">{{
                'money.fiscalStatus.' + option | translate
              }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'fiscal.filterType' | translate }}</mat-label>
          <mat-select [ngModel]="type()" (ngModelChange)="type.set($event); apply()">
            <mat-option [value]="null">{{ 'common.all' | translate }}</mat-option>
            @for (option of types; track option) {
              <mat-option [value]="option">{{
                'money.fiscalType.' + option | translate
              }}</mat-option>
            }
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
          icon="receipt"
          [message]="'fiscal.empty' | translate"
          [hint]="'fiscal.emptyHint' | translate"
        />
      } @else if (rows().length > 0) {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="rows()">
            <ng-container matColumnDef="created">
              <th mat-header-cell *matHeaderCellDef>{{ 'fiscal.colCreated' | translate }}</th>
              <td mat-cell *matCellDef="let f">{{ f.createdAt | dateTime }}</td>
            </ng-container>
            <ng-container matColumnDef="type">
              <th mat-header-cell *matHeaderCellDef>{{ 'fiscal.colType' | translate }}</th>
              <td mat-cell *matCellDef="let f">
                {{ 'money.fiscalType.' + f.type | translate }}
                @if (f.paymentId) {
                  <a class="neo-caption money__block" [routerLink]="['/payments', f.paymentId]">{{
                    'fiscal.openPayment' | translate
                  }}</a>
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'fiscal.colStatus' | translate }}</th>
              <td mat-cell *matCellDef="let f">
                <app-status-chip
                  [status]="f.status"
                  [text]="'money.fiscalStatus.' + f.status | translate"
                />
                @if (f.lastError && f.status !== 'Issued') {
                  <span class="neo-caption money__block">{{ f.lastError }}</span>
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="terminal">
              <th mat-header-cell *matHeaderCellDef>{{ 'fiscal.colTerminal' | translate }}</th>
              <td mat-cell *matCellDef="let f">
                {{ f.terminalId }}
                @if (f.fiscalId) {
                  <span class="neo-caption money__block">{{ f.fiscalId }}</span>
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="amount">
              <th mat-header-cell *matHeaderCellDef class="money__number">
                {{ 'fiscal.colAmount' | translate }}
              </th>
              <td mat-cell *matCellDef="let f" class="money__number">{{ f.amount | money }}</td>
            </ng-container>
            <ng-container matColumnDef="actions">
              <th mat-header-cell *matHeaderCellDef></th>
              <td mat-cell *matCellDef="let f">
                @if (f.status === 'Failed' && canManage()) {
                  <button
                    mat-stroked-button
                    type="button"
                    [disabled]="retrying() === f.id"
                    (click)="retry(f)"
                  >
                    {{ 'fiscal.retry' | translate }}
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
          countKey="fiscal.count"
          (go)="go($event)"
        />
      }
    </div>
  `,
  styleUrl: './money.scss',
  styles: `
    .settings {
      margin-block-end: var(--space-4);
    }
  `,
})
export class FiscalComponent implements OnInit {
  private readonly api = inject(MoneyApiService);
  private readonly auth = inject(AuthService);
  private readonly notify = inject(NotifyService);

  readonly pageSize = pageSize;
  readonly statuses = ['Pending', 'Issued', 'Failed'];
  readonly types = ['Sale', 'Refund', 'TopUp'];
  readonly columns = ['created', 'type', 'status', 'terminal', 'amount', 'actions'];

  readonly rows = signal<readonly FiscalRow[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly status = signal<string | null>(null);
  readonly type = signal<string | null>(null);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);
  readonly retrying = signal<string | null>(null);

  readonly settings = signal<PlatformSetting[]>([]);
  readonly values = signal<Record<string, string>>({});
  readonly saving = signal(false);
  readonly canManage = computed(() => this.auth.can(Permissions.fiscalManage));
  readonly dirty = computed(() => this.settings().some((s) => s.value !== this.values()[s.key]));

  ngOnInit(): void {
    if (this.canManage()) {
      void this.loadSettings();
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

  setValue(key: string, value: string): void {
    this.values.update((all) => ({ ...all, [key]: value }));
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.problem.set(null);

    try {
      const status = this.status();
      const type = this.type();
      const result = await firstValueFrom(
        this.api.listFiscal(status ? [status] : [], type ? [type] : [], this.page(), pageSize),
      );
      this.rows.set(result.items);
      this.total.set(result.total);
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }

  async retry(row: FiscalRow): Promise<void> {
    this.retrying.set(row.id);

    try {
      await firstValueFrom(this.api.retryFiscal(row.id));
      this.notify.success('fiscal.retried');
      await this.load();
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.retrying.set(null);
    }
  }

  async save(): Promise<void> {
    this.saving.set(true);

    try {
      const changed = Object.fromEntries(
        this.settings()
          .filter((s) => s.value !== this.values()[s.key])
          .map((s) => [s.key, this.values()[s.key] ?? '']),
      );
      this.apply$(await firstValueFrom(this.api.updateFiscalSettings(changed)));
      this.notify.success('fiscal.saved');
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.saving.set(false);
    }
  }

  private async loadSettings(): Promise<void> {
    try {
      this.apply$(await firstValueFrom(this.api.fiscalSettings()));
    } catch {
      // The list works without the settings card.
      this.settings.set([]);
    }
  }

  private apply$(settings: PlatformSetting[]): void {
    this.settings.set(settings);
    this.values.set(Object.fromEntries(settings.map((s) => [s.key, s.value])));
  }
}
