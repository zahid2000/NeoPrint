import { Component, OnInit, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatTableModule } from '@angular/material/table';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MonitoringApiService } from '../../core/api/monitoring-api.service';
import { AboutKiosk, HubEvents } from '../../core/realtime/hub-events';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent, StatusTone } from '../../shared/status-chip.component';
import { ServiceTask } from '../operator/operator.models';

/** A task's status in the tone the design system gives it: waiting, under way, over. */
export const taskStatusTones: Readonly<Record<string, StatusTone>> = {
  Open: 'warning',
  Assigned: 'info',
  InProgress: 'info',
  Done: 'success',
  Cancelled: 'neutral',
};

/**
 * Kiosk detail → "Servis tapşırıqları" (plan sections 2.15, 8A.3): what is
 * to be done at this kiosk, who does it and by when - live, as tasks are
 * opened, taken and finished. Managing them is the "Servis tapşırıqları"
 * page's work; here the kiosk's own are read.
 */
@Component({
  selector: 'app-kiosk-tasks',
  imports: [
    FormsModule,
    MatButtonToggleModule,
    MatTableModule,
    TranslatePipe,
    DateTimePipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    StatusChipComponent,
  ],
  template: `
    <div class="tab__head">
      <p class="neo-muted neo-body-small">{{ 'monitoring.tasks.description' | translate }}</p>
      <mat-button-toggle-group
        [attr.aria-label]="'monitoring.filter' | translate"
        [ngModel]="openOnly()"
        (ngModelChange)="setOpenOnly($event)"
      >
        <mat-button-toggle [value]="true">{{ 'monitoring.filterOpen' | translate }}</mat-button-toggle>
        <mat-button-toggle [value]="false">{{ 'monitoring.filterAll' | translate }}</mat-button-toggle>
      </mat-button-toggle-group>
    </div>

    @if (problem(); as problem) {
      <app-error-banner [problem]="problem" effect="monitoring.tasks.loadFailedEffect" (retry)="load()" />
    } @else if (tasks() === null) {
      <div class="neo-skeleton neo-skeleton--block"></div>
    } @else if (tasks()!.length === 0) {
      <app-empty-state
        [icon]="openOnly() ? 'task_alt' : 'inbox'"
        [message]="(openOnly() ? 'monitoring.tasks.noneOpen' : 'monitoring.tasks.none') | translate"
      />
    } @else {
      <div class="neo-table-wrapper">
        <table mat-table class="neo-table" [dataSource]="tasks()!">
          <ng-container matColumnDef="type">
            <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.tasks.colType' | translate }}</th>
            <td mat-cell *matCellDef="let task">
              {{ 'operator.taskType.' + task.type | translate }}
              @if (task.notes) {
                <span class="neo-caption tab__block">{{ task.notes }}</span>
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
          <ng-container matColumnDef="created">
            <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.tasks.colCreated' | translate }}</th>
            <td mat-cell *matCellDef="let task" class="neo-nowrap">{{ task.createdAt | dateTime }}</td>
          </ng-container>
          <ng-container matColumnDef="due">
            <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.tasks.colDue' | translate }}</th>
            <td mat-cell *matCellDef="let task" class="neo-nowrap">{{ task.dueAt | dateTime }}</td>
          </ng-container>

          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
        </table>
      </div>
    }
  `,
  styleUrl: './kiosk-tab.scss',
})
export class KioskTasksComponent implements OnInit {
  private readonly api = inject(MonitoringApiService);

  readonly kioskId = input.required<string>();

  readonly columns = ['type', 'priority', 'status', 'assignee', 'created', 'due'];
  readonly tones = taskStatusTones;

  readonly openOnly = signal(true);
  readonly tasks = signal<readonly ServiceTask[] | null>(null);
  readonly problem = signal<Problem | null>(null);

  constructor() {
    liveRefresh({
      events: [HubEvents.serviceTaskAssigned, HubEvents.serviceTaskUpdated],
      accepts: (_event, payload) => (payload as AboutKiosk | null)?.kioskId === this.kioskId(),
      reload: () => this.load(true),
    });
  }

  ngOnInit(): void {
    void this.load();
  }

  setOpenOnly(openOnly: boolean): void {
    this.openOnly.set(openOnly);
    this.tasks.set(null);
    void this.load();
  }

  async load(quiet = false): Promise<void> {
    if (!quiet) {
      this.problem.set(null);
    }

    try {
      this.tasks.set((await firstValueFrom(this.api.kioskTasks(this.kioskId(), this.openOnly()))).items);
    } catch (error) {
      if (!quiet) {
        this.problem.set(toProblem(error));
      }
    }
  }
}
