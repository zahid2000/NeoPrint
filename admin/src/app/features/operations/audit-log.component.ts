import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, AdminUser } from '../../core/api/admin-api.service';
import { OperationsApiService } from '../../core/api/operations-api.service';
import { AuditLogEntry } from '../../core/api/step7.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { provideNeoDates } from '../../shared/date-input';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { FilterBarComponent } from '../../shared/filter-bar.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { PagerComponent } from '../money/money-shared';

const pageSize = 50;

/** One field of a before/after pair; `changed` marks the ones that differ. */
export interface DiffRow {
  readonly field: string;
  readonly before: string;
  readonly after: string;
  readonly changed: boolean;
}

function parse(text: string | null | undefined): Record<string, unknown> | null {
  if (!text) {
    return null;
  }

  try {
    const value: unknown = JSON.parse(text);

    return value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

function show(value: unknown): string {
  return value === undefined ? '' : typeof value === 'string' ? value : JSON.stringify(value);
}

/**
 * Both sides as JSON objects: field by field, the changed ones marked. Null
 * when either side is not an object - the page then shows the two texts.
 */
export function diff(before: string | null | undefined, after: string | null | undefined): DiffRow[] | null {
  const old = parse(before);
  const now = parse(after);

  if ((before && !old) || (after && !now) || (!old && !now)) {
    return null;
  }

  const fields = [...new Set([...Object.keys(old ?? {}), ...Object.keys(now ?? {})])].sort();

  return fields.map((field) => {
    const was = show(old?.[field]);
    const is = show(now?.[field]);

    return { field, before: was, after: is, changed: was !== is };
  });
}

/**
 * "Audit log" (plan section 8A.3): who did what to what and when, with what
 * it was before and after, newest first. Read only; an account with a kiosk
 * scope reads what was done to its own kiosks.
 */
@Component({
  selector: 'app-audit-log',
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
    DateTimePipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    FilterBarComponent,
    PageHeaderComponent,
    PagerComponent,
    StatusChipComponent,
  ],
  providers: [provideNeoDates()],
  template: `
    <div class="neo-page">
      <app-page-header [title]="'ops.audit.title' | translate" [description]="'ops.audit.description' | translate" />

      <app-filter-bar>
        <mat-form-field>
          <mat-label>{{ 'ops.audit.action' | translate }}</mat-label>
          <input matInput [placeholder]="'ops.audit.actionHint' | translate" [ngModel]="action()" (ngModelChange)="action.set($event)" (keyup.enter)="apply()" />
        </mat-form-field>
        @if (admins().length > 0) {
          <mat-form-field>
            <mat-label>{{ 'ops.audit.who' | translate }}</mat-label>
            <mat-select [ngModel]="adminUserId()" (ngModelChange)="adminUserId.set($event); apply()">
              <mat-option [value]="null">{{ 'ops.audit.anyone' | translate }}</mat-option>
              @for (admin of admins(); track admin.id) {
                <mat-option [value]="admin.id">{{ admin.fullName }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        }
        <mat-form-field>
          <mat-label>{{ 'ops.audit.entityType' | translate }}</mat-label>
          <input matInput [ngModel]="entityType()" (ngModelChange)="entityType.set($event)" (keyup.enter)="apply()" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'ops.audit.entityId' | translate }}</mat-label>
          <input matInput [ngModel]="entityId()" (ngModelChange)="entityId.set($event)" (keyup.enter)="apply()" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'ops.audit.period' | translate }}</mat-label>
          <mat-date-range-input [rangePicker]="period">
            <input matStartDate [placeholder]="'date.hint' | translate" [ngModel]="from()" (ngModelChange)="from.set($event)" />
            <input matEndDate [placeholder]="'date.hint' | translate" [ngModel]="to()" (ngModelChange)="to.set($event); apply()" />
          </mat-date-range-input>
          <mat-datepicker-toggle matIconSuffix [for]="period" />
          <mat-date-range-picker #period />
        </mat-form-field>
        <button mat-flat-button type="button" (click)="apply()">{{ 'ops.audit.find' | translate }}</button>
        @if (filtered()) {
          <button mat-button type="button" (click)="clear()">{{ 'ops.audit.clear' | translate }}</button>
        }
      </app-filter-bar>

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="ops.audit.loadFailedEffect" (retry)="load()" />
      } @else if (rows() === null) {
        <div class="neo-skeleton neo-skeleton--block"></div>
      } @else if (rows()!.length === 0) {
        <app-empty-state icon="history" [message]="(filtered() ? 'ops.audit.noMatch' : 'ops.audit.empty') | translate">
          @if (filtered()) {
            <button mat-stroked-button type="button" (click)="clear()">{{ 'ops.audit.clear' | translate }}</button>
          }
        </app-empty-state>
      } @else {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" multiTemplateDataRows [dataSource]="rows()!">
            <ng-container matColumnDef="at">
              <th mat-header-cell *matHeaderCellDef>{{ 'ops.audit.at' | translate }}</th>
              <td mat-cell *matCellDef="let entry" class="neo-nowrap">{{ entry.at | dateTime }}</td>
            </ng-container>
            <ng-container matColumnDef="who">
              <th mat-header-cell *matHeaderCellDef>{{ 'ops.audit.who' | translate }}</th>
              <td mat-cell *matCellDef="let entry">
                @if (entry.actorType === 'Admin') {
                  {{ entry.adminName ?? ('ops.audit.unknownAdmin' | translate) }}
                } @else {
                  <app-status-chip [status]="entry.actorType" toneOverride="neutral" [text]="'jobs.actor.' + entry.actorType | translate" />
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="action">
              <th mat-header-cell *matHeaderCellDef>{{ 'ops.audit.action' | translate }}</th>
              <td mat-cell *matCellDef="let entry"><code>{{ entry.action }}</code></td>
            </ng-container>
            <ng-container matColumnDef="entity">
              <th mat-header-cell *matHeaderCellDef>{{ 'ops.audit.entity' | translate }}</th>
              <td mat-cell *matCellDef="let entry">
                {{ entry.entityType }}
                @if (entry.entityId) {
                  <span class="neo-caption ops__block"><code>{{ entry.entityId }}</code></span>
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="reason">
              <th mat-header-cell *matHeaderCellDef>{{ 'ops.audit.reason' | translate }}</th>
              <td mat-cell *matCellDef="let entry">{{ entry.reason }}</td>
            </ng-container>

            <ng-container matColumnDef="detail">
              <td mat-cell *matCellDef="let entry" [attr.colspan]="columns.length">
                @if (open() === entry.id) {
                  @if (changes(entry); as rows) {
                    <table class="ops__diff">
                      <thead>
                        <tr>
                          <th>{{ 'ops.audit.field' | translate }}</th>
                          <th>{{ 'ops.audit.before' | translate }}</th>
                          <th>{{ 'ops.audit.after' | translate }}</th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (row of rows; track row.field) {
                          <tr [class.ops__changed]="row.changed">
                            <td><code>{{ row.field }}</code></td>
                            <td>{{ row.before }}</td>
                            <td>{{ row.after }}</td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  } @else {
                    <table class="ops__diff">
                      <tbody>
                        <tr>
                          <th>{{ 'ops.audit.before' | translate }}</th>
                          <td>{{ entry.before ?? '—' }}</td>
                        </tr>
                        <tr>
                          <th>{{ 'ops.audit.after' | translate }}</th>
                          <td>{{ entry.after ?? '—' }}</td>
                        </tr>
                      </tbody>
                    </table>
                  }
                  <p class="neo-caption">
                    @if (entry.ip) {
                      IP: <code>{{ entry.ip }}</code> ·
                    }
                    CorrelationId: <code>{{ entry.correlationId ?? '—' }}</code>
                  </p>
                }
              </td>
            </ng-container>

            <tr mat-header-row *matHeaderRowDef="columns"></tr>
            <tr
              mat-row
              *matRowDef="let row; columns: columns"
              class="ops__clickable"
              [class.ops__row--open]="open() === row.id"
              (click)="toggle(row)"
            ></tr>
            <tr mat-row *matRowDef="let row; columns: ['detail']; when: isOpen" class="ops__detail"></tr>
          </table>
        </div>
        <app-money-pager [total]="total()" [page]="page()" [pageSize]="pageSize" countKey="ops.audit.count" (go)="go($event)" />
      }
    </div>
  `,
  styleUrl: './operations.scss',
})
export class AuditLogComponent {
  private readonly api = inject(OperationsApiService);
  private readonly admin = inject(AdminApiService);
  private readonly auth = inject(AuthService);

  readonly pageSize = pageSize;
  readonly columns = ['at', 'who', 'action', 'entity', 'reason'];

  readonly action = signal('');
  readonly entityType = signal('');
  readonly entityId = signal('');
  readonly adminUserId = signal<string | null>(null);
  readonly from = signal<Date | null>(null);
  readonly to = signal<Date | null>(null);
  readonly page = signal(1);
  readonly rows = signal<readonly AuditLogEntry[] | null>(null);
  readonly total = signal(0);
  readonly problem = signal<Problem | null>(null);
  readonly open = signal<string | null>(null);
  readonly admins = signal<readonly AdminUser[]>([]);

  readonly filtered = computed(
    () => !!(this.action().trim() || this.entityType().trim() || this.entityId().trim() || this.adminUserId() || this.from() || this.to()),
  );

  readonly isOpen = (_index: number, row: AuditLogEntry): boolean => this.open() === row.id;

  constructor() {
    // Who did it is chosen from the accounts - only for someone who may list them.
    if (this.auth.can(Permissions.adminsManage)) {
      firstValueFrom(this.admin.listAdmins())
        .then((admins) => this.admins.set(admins))
        .catch(() => undefined);
    }

    void this.load();
  }

  /** Opens or closes an entry's change; the table works out its detail rows only when its data changes. */
  toggle(entry: AuditLogEntry): void {
    this.open.set(this.open() === entry.id ? null : entry.id);
    this.rows.update((rows) => (rows ? [...rows] : rows));
  }

  changes(entry: AuditLogEntry): DiffRow[] | null {
    return diff(entry.before, entry.after);
  }

  apply(): void {
    this.page.set(1);
    this.open.set(null);
    void this.load();
  }

  clear(): void {
    this.action.set('');
    this.entityType.set('');
    this.entityId.set('');
    this.adminUserId.set(null);
    this.from.set(null);
    this.to.set(null);
    this.apply();
  }

  go(page: number): void {
    this.page.set(page);
    void this.load();
  }

  async load(): Promise<void> {
    this.problem.set(null);
    this.rows.set(null);

    const to = this.to();

    try {
      const result = await firstValueFrom(
        this.api.auditLogs(
          {
            action: this.action(),
            entityType: this.entityType(),
            entityId: this.entityId(),
            adminUserId: this.adminUserId() ?? undefined,
            from: this.from(),
            // The whole of the last day.
            to: to ? new Date(to.getFullYear(), to.getMonth(), to.getDate(), 23, 59, 59) : null,
          },
          this.page(),
          pageSize,
        ),
      );
      this.rows.set(result.items);
      this.total.set(result.total);
    } catch (error) {
      this.problem.set(toProblem(error));
    }
  }
}
