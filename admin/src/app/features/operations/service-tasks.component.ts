import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { OperationsApiService, TaskView } from '../../core/api/operations-api.service';
import { ServiceTaskDetail, ServiceTaskSummary } from '../../core/api/step7.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { HubEvents } from '../../core/realtime/hub-events';
import { KioskLiveStore } from '../../core/realtime/kiosk-live.store';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { FilterBarComponent } from '../../shared/filter-bar.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { taskStatusTones } from '../kiosks/kiosk-tasks.component';
import { PagerComponent } from '../money/money-shared';
import { NewTaskDialogComponent, TaskDialogComponent } from './task.dialogs';

const pageSize = 20;

/**
 * "Servis tapşırıqları" (plan sections 2.15, 8A.3): every task in the
 * account's kiosks with the SLA figures above it - what is open, what is
 * late, what nobody has, how long a task takes - live as operators take and
 * finish them. A new task, its holder, its urgency and calling it off are
 * here; doing it is the operator panel's.
 */
@Component({
  selector: 'app-service-tasks',
  imports: [
    FormsModule,
    RouterLink,
    MatButtonModule,
    MatButtonToggleModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatIconModule,
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
  template: `
    <div class="neo-page">
      <app-page-header [title]="'ops.tasks.title' | translate" [description]="'ops.tasks.description' | translate">
        @if (canManage()) {
          <button mat-flat-button type="button" (click)="create()">
            <mat-icon>add</mat-icon>
            {{ 'ops.tasks.new' | translate }}
          </button>
        }
      </app-page-header>

      @if (summary(); as figures) {
        <div class="ops__stats">
          <div class="neo-card ops__stat">
            <span class="neo-caption">{{ 'ops.tasks.statOpen' | translate }}</span>
            <span class="ops__stat-value">{{ figures.open }}</span>
          </div>
          <div class="neo-card ops__stat" [class.ops__stat--danger]="figures.overdue > 0">
            <span class="neo-caption">{{ 'ops.tasks.statOverdue' | translate }}</span>
            <span class="ops__stat-value">{{ figures.overdue }}</span>
          </div>
          <div class="neo-card ops__stat">
            <span class="neo-caption">{{ 'ops.tasks.statPool' | translate }}</span>
            <span class="ops__stat-value">{{ figures.unassigned }}</span>
          </div>
          <div class="neo-card ops__stat">
            <span class="neo-caption">{{ 'ops.tasks.statAverage' | translate }}</span>
            <span class="ops__stat-value">{{ average(figures) }}</span>
            <span class="neo-caption">{{ 'ops.tasks.statAverageHint' | translate: { count: figures.completedLastWeek } }}</span>
          </div>
        </div>
      }

      <app-filter-bar>
        <mat-button-toggle-group [attr.aria-label]="'monitoring.filter' | translate" [ngModel]="view()" (ngModelChange)="setView($event)">
          <mat-button-toggle value="open">{{ 'ops.tasks.viewOpen' | translate }}</mat-button-toggle>
          <mat-button-toggle value="closed">{{ 'ops.tasks.viewClosed' | translate }}</mat-button-toggle>
          <mat-button-toggle value="all">{{ 'monitoring.filterAll' | translate }}</mat-button-toggle>
        </mat-button-toggle-group>
        <mat-form-field>
          <mat-label>{{ 'ops.kiosk' | translate }}</mat-label>
          <mat-select [ngModel]="kioskId()" (ngModelChange)="setKiosk($event)">
            <mat-option [value]="null">{{ 'ops.allKiosks' | translate }}</mat-option>
            @for (kiosk of kiosks(); track kiosk.id) {
              <mat-option [value]="kiosk.id">{{ kiosk.code }} — {{ kiosk.name }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-checkbox [ngModel]="unassigned()" (ngModelChange)="setUnassigned($event)">{{ 'ops.tasks.onlyPool' | translate }}</mat-checkbox>
      </app-filter-bar>

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="ops.tasks.loadFailedEffect" (retry)="load()" />
      } @else if (rows() === null) {
        <div class="neo-skeleton neo-skeleton--block"></div>
      } @else if (rows()!.length === 0) {
        <app-empty-state
          [icon]="view() === 'open' ? 'task_alt' : 'inbox'"
          [message]="(view() === 'open' ? 'ops.tasks.noneOpen' : 'ops.tasks.none') | translate"
        />
      } @else {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="rows()!">
            <ng-container matColumnDef="kiosk">
              <th mat-header-cell *matHeaderCellDef>{{ 'ops.kiosk' | translate }}</th>
              <td mat-cell *matCellDef="let task" class="neo-nowrap">
                <a [routerLink]="['/kiosks', task.kioskId]" (click)="$event.stopPropagation()">{{ task.kioskCode }}</a>
              </td>
            </ng-container>
            <ng-container matColumnDef="type">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.tasks.colType' | translate }}</th>
              <td mat-cell *matCellDef="let task">
                {{ 'operator.taskType.' + task.type | translate }}
                @if (task.notes) {
                  <span class="neo-caption ops__block">{{ task.notes }}</span>
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="priority">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.tasks.colPriority' | translate }}</th>
              <td mat-cell *matCellDef="let task">
                <app-status-chip [status]="task.priority" [text]="'operator.priority.' + task.priority | translate" />
              </td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.tasks.colStatus' | translate }}</th>
              <td mat-cell *matCellDef="let task">
                <app-status-chip
                  [status]="task.status"
                  [toneOverride]="tones[task.status]"
                  [text]="'operator.taskStatus.' + task.status | translate"
                />
              </td>
            </ng-container>
            <ng-container matColumnDef="assignee">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.tasks.colAssignee' | translate }}</th>
              <td mat-cell *matCellDef="let task">
                @if (task.assignedToName) {
                  {{ task.assignedToName }}
                } @else {
                  <span class="neo-caption">{{ 'monitoring.tasks.unassigned' | translate }}</span>
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="due">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.tasks.colDue' | translate }}</th>
              <td mat-cell *matCellDef="let task" class="neo-nowrap">
                @if (late(task)) {
                  <span class="ops__late">
                    <mat-icon aria-hidden="true">timer_off</mat-icon>
                    {{ task.dueAt | dateTime }} · {{ 'ops.tasks.late' | translate }}
                  </span>
                } @else {
                  {{ task.dueAt | dateTime }}
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="created">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.tasks.colCreated' | translate }}</th>
              <td mat-cell *matCellDef="let task" class="neo-nowrap">{{ task.createdAt | dateTime }}</td>
            </ng-container>

            <tr mat-header-row *matHeaderRowDef="columns"></tr>
            <tr mat-row *matRowDef="let row; columns: columns" class="ops__clickable" (click)="openTask(row)"></tr>
          </table>
        </div>
        <app-money-pager [total]="total()" [page]="page()" [pageSize]="pageSize" countKey="ops.tasks.count" (go)="go($event)" />
      }
    </div>
  `,
  styleUrl: './operations.scss',
})
export class ServiceTasksComponent {
  private readonly api = inject(OperationsApiService);
  private readonly auth = inject(AuthService);
  private readonly kioskStore = inject(KioskLiveStore);
  private readonly dialog = inject(MatDialog);
  private readonly toast = inject(NotifyService);
  private readonly translate = inject(TranslateService);

  readonly pageSize = pageSize;
  readonly columns = ['kiosk', 'type', 'priority', 'status', 'assignee', 'due', 'created'];
  readonly tones = taskStatusTones;

  readonly view = signal<TaskView>('open');
  readonly kioskId = signal<string | null>(null);
  readonly unassigned = signal(false);
  readonly page = signal(1);
  readonly rows = signal<readonly ServiceTaskDetail[] | null>(null);
  readonly total = signal(0);
  readonly summary = signal<ServiceTaskSummary | null>(null);
  readonly problem = signal<Problem | null>(null);

  readonly canManage = computed(() => this.auth.can(Permissions.serviceTasksManage));
  readonly kiosks = computed(() => this.kioskStore.kiosks() ?? []);

  constructor() {
    this.kioskStore.watch();

    liveRefresh({
      events: [HubEvents.serviceTaskAssigned, HubEvents.serviceTaskUpdated],
      reload: () => this.load(true),
    });

    void this.load();
  }

  setView(view: TaskView): void {
    this.view.set(view);
    this.restart();
  }

  setKiosk(kioskId: string | null): void {
    this.kioskId.set(kioskId);
    this.restart();
  }

  setUnassigned(unassigned: boolean): void {
    this.unassigned.set(unassigned);
    this.restart();
  }

  go(page: number): void {
    this.page.set(page);
    void this.load();
  }

  late(task: ServiceTaskDetail): boolean {
    return !!task.dueAt && ['Open', 'Assigned', 'InProgress'].includes(task.status) && Date.parse(task.dueAt) < Date.now();
  }

  /** "2 saat 15 dəq", or "—" when nothing was finished this week. */
  average(figures: ServiceTaskSummary): string {
    const minutes = figures.averageMinutes;

    if (minutes === null || minutes === undefined) {
      return '—';
    }

    const hours = Math.floor(minutes / 60);

    return (
      hours > 0
        ? this.translate.instant('monitoring.duration.hours', { hours, minutes: minutes % 60 })
        : this.translate.instant('monitoring.duration.minutes', { minutes })
    ) as string;
  }

  async load(quiet = false): Promise<void> {
    if (!quiet) {
      this.problem.set(null);
    }

    try {
      const [result, summary] = await Promise.all([
        firstValueFrom(
          this.api.tasks({
            view: this.view(),
            kioskId: this.kioskId(),
            unassigned: this.unassigned(),
            page: this.page(),
            pageSize,
          }),
        ),
        firstValueFrom(this.api.taskSummary()),
      ]);

      this.rows.set(result.items);
      this.total.set(result.total);
      this.summary.set(summary);
    } catch (error) {
      if (!quiet) {
        this.problem.set(toProblem(error));
      }
    }
  }

  create(): void {
    this.dialog
      .open(NewTaskDialogComponent, { data: { kiosks: this.kiosks() }, width: '640px', maxWidth: 'calc(100vw - 32px)' })
      .afterClosed()
      .subscribe((saved) => {
        if (saved) {
          this.toast.success('ops.tasks.created');
          void this.load(true);
        }
      });
  }

  openTask(task: ServiceTaskDetail): void {
    this.dialog
      .open(TaskDialogComponent, { data: { task }, width: '640px', maxWidth: 'calc(100vw - 32px)' })
      .afterClosed()
      .subscribe((saved) => {
        if (saved) {
          this.toast.success('ops.tasks.saved');
          void this.load(true);
        }
      });
  }

  private restart(): void {
    this.page.set(1);
    this.rows.set(null);
    void this.load();
  }
}
