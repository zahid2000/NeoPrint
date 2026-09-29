import { Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { Router } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, KioskListItem } from '../../core/api/admin-api.service';
import { PrintJobListItem, printJobStatuses } from '../../core/api/step3.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { FilterBarComponent } from '../../shared/filter-bar.component';
import { provideNeoDates } from '../../shared/date-input';
import { DateTimePipe, MoneyPipe } from '../../shared/format.pipes';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';

const pageSize = 20;

/** How long the list waits after a queue change before it asks again, so a burst is one request. */
const refreshDelayMs = 1000;

/** The first moment of the chosen day in the viewer's time zone, as the API expects it. */
const startOfDay = (date: Date | null): string | null =>
  date ? new Date(date.getFullYear(), date.getMonth(), date.getDate()).toISOString() : null;

/** The last moment of the chosen day, so "to 29.09" includes the whole of the 29th. */
const endOfDay = (date: Date | null): string | null =>
  date
    ? new Date(date.getFullYear(), date.getMonth(), date.getDate(), 23, 59, 59, 999).toISOString()
    : null;

/**
 * The "Print job-lar" page (plan section 8A.3): every order, newest first,
 * filtered by status, kiosk, time and code. The same list sits in the kiosk
 * detail's Orders tab with the kiosk fixed.
 */
@Component({
  selector: 'app-print-jobs',
  imports: [
    FormsModule,
    MatButtonModule,
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
    DateTimePipe,
    MoneyPipe,
  ],
  // dd.MM.yyyy in every language, whatever the browser's own is (docs/DESIGN_SYSTEM.md, "Tarix sahəsi").
  providers: [provideNeoDates()],
  template: `
    <div [class.neo-page]="!embedded()">
      @if (!embedded()) {
        <app-page-header
          [title]="'jobs.title' | translate"
          [description]="'jobs.description' | translate"
        />
      }

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="jobs.loadFailedEffect" (retry)="load()" />
      }

      <app-filter-bar>
        <mat-form-field>
          <mat-label>{{ 'jobs.filterCode' | translate }}</mat-label>
          <mat-icon matPrefix>search</mat-icon>
          <input
            matInput
            [ngModel]="code()"
            (ngModelChange)="code.set($event)"
            (keyup.enter)="search()"
            (blur)="search()"
          />
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'jobs.filterStatus' | translate }}</mat-label>
          <mat-select
            multiple
            [ngModel]="statuses()"
            (ngModelChange)="statuses.set($event); search()"
          >
            @for (option of statusOptions; track option) {
              <mat-option [value]="option">{{ 'jobs.status.' + option | translate }}</mat-option>
            }
          </mat-select>
        </mat-form-field>

        @if (!embedded() && kiosks().length > 0) {
          <mat-form-field>
            <mat-label>{{ 'jobs.filterKiosk' | translate }}</mat-label>
            <mat-select [ngModel]="kiosk()" (ngModelChange)="kiosk.set($event); search()">
              <mat-option [value]="null">{{ 'jobs.filterAllKiosks' | translate }}</mat-option>
              @for (option of kiosks(); track option.id) {
                <mat-option [value]="option.id">{{ option.code }} — {{ option.name }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        }

        <!-- The format is the placeholder: a filter row has no hint line (docs/DESIGN_SYSTEM.md, "Tarix sahəsi"). -->
        <mat-form-field class="jobs__period" subscriptSizing="dynamic">
          <mat-label>{{ 'jobs.filterPeriod' | translate }}</mat-label>
          <mat-date-range-input [rangePicker]="period">
            <input
              matStartDate
              [placeholder]="'date.hint' | translate"
              [ngModel]="from()"
              (ngModelChange)="from.set(validDate($event)); search()"
            />
            <input
              matEndDate
              [placeholder]="'date.hint' | translate"
              [ngModel]="to()"
              (ngModelChange)="to.set(validDate($event)); search()"
            />
          </mat-date-range-input>
          <mat-datepicker-toggle matIconSuffix [for]="period" />
          <mat-date-range-picker #period />
          <mat-error>{{ 'date.invalid' | translate }}</mat-error>
        </mat-form-field>
      </app-filter-bar>

      @if (loading()) {
        <div class="neo-card">
          @for (row of [1, 2, 3, 4]; track row) {
            <div class="neo-skeleton neo-skeleton--row"></div>
          }
        </div>
      } @else if (jobs().length === 0 && !problem()) {
        @if (filtersActive()) {
          <app-empty-state icon="search_off" [message]="'jobs.emptyFiltered' | translate">
            <button mat-stroked-button type="button" (click)="clearFilters()">
              {{ 'jobs.clearFilters' | translate }}
            </button>
          </app-empty-state>
        } @else {
          <app-empty-state
            icon="receipt_long"
            [message]="'jobs.empty' | translate"
            [hint]="'jobs.emptyHint' | translate"
          />
        }
      } @else if (jobs().length > 0) {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="jobs()">
            <ng-container matColumnDef="code">
              <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colCode' | translate }}</th>
              <td mat-cell *matCellDef="let job">
                <strong>{{ job.jobCode }}</strong>
              </td>
            </ng-container>
            <ng-container matColumnDef="kiosk">
              <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colKiosk' | translate }}</th>
              <td mat-cell *matCellDef="let job">{{ job.kioskCode }}</td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colStatus' | translate }}</th>
              <td mat-cell *matCellDef="let job">
                <app-status-chip
                  [status]="job.status"
                  [text]="'jobs.status.' + job.status | translate"
                />
              </td>
            </ng-container>
            <ng-container matColumnDef="file">
              <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colFile' | translate }}</th>
              <td mat-cell *matCellDef="let job" class="jobs__file">
                {{ job.fileName ?? ('jobs.noFile' | translate) }}
                <span class="neo-caption jobs__block">{{
                  'jobs.source.' + job.source | translate
                }}</span>
              </td>
            </ng-container>
            <ng-container matColumnDef="pages">
              <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colPages' | translate }}</th>
              <td mat-cell *matCellDef="let job">{{ job.selectedPages }} × {{ job.copies }}</td>
            </ng-container>
            <ng-container matColumnDef="total">
              <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colTotal' | translate }}</th>
              <td mat-cell *matCellDef="let job">
                {{ job.total | money }}
                @if (job.paymentSkipped) {
                  <span class="neo-caption jobs__block">{{ 'jobs.devPayment' | translate }}</span>
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="created">
              <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colCreated' | translate }}</th>
              <td mat-cell *matCellDef="let job">{{ job.createdAt | dateTime }}</td>
            </ng-container>
            <ng-container matColumnDef="finished">
              <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colFinished' | translate }}</th>
              <td mat-cell *matCellDef="let job">
                @if (job.finishedAt) {
                  {{ job.finishedAt | dateTime }}
                }
              </td>
            </ng-container>

            <tr mat-header-row *matHeaderRowDef="columns()"></tr>
            <tr
              mat-row
              class="neo-table__row--clickable"
              tabindex="0"
              *matRowDef="let row; columns: columns()"
              (click)="open(row)"
              (keydown.enter)="open(row)"
            ></tr>
          </table>
        </div>

        <div class="jobs__pager">
          <span class="neo-caption">{{ 'jobs.count' | translate: { total: total() } }}</span>
          @if (pages() > 1) {
            <button mat-button type="button" [disabled]="page() === 1" (click)="go(page() - 1)">
              <mat-icon>chevron_left</mat-icon>
              {{ 'jobs.previous' | translate }}
            </button>
            <span class="neo-caption">{{
              'jobs.pageOf' | translate: { page: page(), pages: pages() }
            }}</span>
            <button
              mat-button
              type="button"
              [disabled]="page() === pages()"
              (click)="go(page() + 1)"
            >
              {{ 'jobs.next' | translate }}
              <mat-icon iconPositionEnd>chevron_right</mat-icon>
            </button>
          }
        </div>
      }
    </div>
  `,
  styles: `
    .jobs__block {
      display: block;
    }

    /* Wide enough for two dates; an error wraps under the field instead of widening the row. */
    .jobs__period {
      width: 280px;
    }

    .jobs__file {
      max-width: 280px;
      overflow-wrap: anywhere;
    }

    .jobs__pager {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-3);
      align-items: center;
      justify-content: flex-end;
      margin-block: var(--space-4);
    }

    app-filter-bar {
      display: block;
      margin-block-end: var(--space-4);
    }
  `,
})
export class PrintJobsComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly auth = inject(AuthService);
  private readonly hub = inject(AdminHubService);
  private readonly router = inject(Router);

  /** Set when the list sits in a kiosk's Orders tab: the kiosk filter is fixed. */
  readonly kioskId = input<string | undefined>(undefined);

  readonly statusOptions = printJobStatuses;

  readonly jobs = signal<readonly PrintJobListItem[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);
  readonly kiosks = signal<KioskListItem[]>([]);

  readonly code = signal('');
  readonly statuses = signal<string[]>([]);
  readonly kiosk = signal<string | null>(null);
  readonly from = signal<Date | null>(null);
  readonly to = signal<Date | null>(null);

  readonly embedded = computed(() => !!this.kioskId());

  readonly columns = computed(() =>
    this.embedded()
      ? ['code', 'status', 'file', 'pages', 'total', 'created', 'finished']
      : ['code', 'kiosk', 'status', 'file', 'pages', 'total', 'created', 'finished'],
  );

  readonly pages = computed(() => Math.max(1, Math.ceil(this.total() / pageSize)));

  readonly filtersActive = computed(
    () =>
      this.code().trim() !== '' ||
      this.statuses().length > 0 ||
      (!this.embedded() && this.kiosk() !== null) ||
      this.from() !== null ||
      this.to() !== null,
  );

  /** The filters the last request used, so an unchanged code field does not reload on blur. */
  private lastQuery = '';
  private refreshTimer: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    // A queue change means a job moved: the list follows without a reload button.
    const off = this.hub.on<{ kioskId: string }>('QueueUpdated', (event) => {
      const kiosk = this.kioskId() ?? this.kiosk();

      if (kiosk && kiosk !== event.kioskId) {
        return;
      }

      clearTimeout(this.refreshTimer);
      this.refreshTimer = setTimeout(() => void this.load(false), refreshDelayMs);
    });

    inject(DestroyRef).onDestroy(() => {
      off();
      clearTimeout(this.refreshTimer);
    });
  }

  ngOnInit(): void {
    if (!this.embedded() && this.auth.can(Permissions.kiosksView)) {
      firstValueFrom(this.api.listKiosks())
        .then((kiosks) => this.kiosks.set(kiosks))
        // The kiosk filter is a convenience: without it the list still works.
        .catch(() => this.kiosks.set([]));
    }

    void this.load();
  }

  search(): void {
    this.page.set(1);
    void this.load();
  }

  go(page: number): void {
    this.page.set(page);
    void this.load();
  }

  clearFilters(): void {
    this.code.set('');
    this.statuses.set([]);
    this.kiosk.set(null);
    this.from.set(null);
    this.to.set(null);
    this.search();
  }

  /** A half-typed or wrong date is not a filter: the field shows why, the list ignores it. */
  validDate(value: Date | null): Date | null {
    return value && !Number.isNaN(value.getTime()) ? value : null;
  }

  open(job: PrintJobListItem): void {
    void this.router.navigate(['/print-jobs', job.id]);
  }

  async load(showSkeleton = true): Promise<void> {
    const filter = {
      kioskId: this.kioskId() ?? this.kiosk(),
      statuses: this.statuses(),
      code: this.code(),
      from: startOfDay(this.from()),
      to: endOfDay(this.to()),
      page: this.page(),
      pageSize,
    };

    const query = JSON.stringify(filter);

    if (showSkeleton && query === this.lastQuery && !this.problem() && !this.loading()) {
      return;
    }

    this.lastQuery = query;

    if (showSkeleton) {
      this.loading.set(true);
    }

    this.problem.set(null);

    try {
      const result = await firstValueFrom(this.api.listPrintJobs(filter));
      this.jobs.set(result.items);
      this.total.set(result.total);
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }
}
