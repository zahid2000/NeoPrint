import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { OperatorActionsService } from './operator-actions.service';
import { OperatorApiService } from './operator-api.service';
import { OperatorFreshnessComponent } from './operator-freshness.component';
import { OperatorNotifyService } from './operator-notify.service';
import { OperatorTasksStore } from './operator-tasks.store';
import { ServiceTask } from './operator.models';

/**
 * "Tapşırıqlarım" (plan section 8B): what was given to this operator, and what
 * waits in the pool for whoever takes it. One button a task, and it is always
 * the next step: Qəbul et → Başla → Tamamla.
 */
@Component({
  selector: 'app-operator-tasks',
  imports: [
    NgTemplateOutlet,
    RouterLink,
    MatButtonModule,
    MatIconModule,
    TranslatePipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    StatusChipComponent,
    DateTimePipe,
    OperatorFreshnessComponent,
  ],
  template: `
    <div class="op-page">
      <h1 class="op-title">{{ 'operator.tasks.title' | translate }}</h1>

      @if (notify.access() === 'default') {
        <div class="neo-notice" role="status">
          <mat-icon aria-hidden="true">info</mat-icon>
          <span>{{ 'operator.tasks.notifyHint' | translate }}</span>
          <button mat-stroked-button type="button" (click)="notify.enable()">
            {{ 'operator.tasks.notifyEnable' | translate }}
          </button>
        </div>
      }

      @if (store.problem(); as problem) {
        <app-error-banner [problem]="problem" effect="operator.loadFailedEffect" (retry)="store.reload()" />
      } @else if (store.loading()) {
        @for (row of [1, 2]; track row) {
          <div class="neo-skeleton neo-skeleton--block"></div>
        }
      } @else if (store.mine().length === 0 && store.pool().length === 0) {
        <app-empty-state
          icon="task_alt"
          [message]="'operator.tasks.empty' | translate"
          [hint]="'operator.tasks.emptyHint' | translate"
        />
      } @else {
        <app-operator-freshness [at]="store.at()" [offline]="store.offline()" />

        <h2 class="op-section">{{ 'operator.tasks.mine' | translate }}</h2>
        @for (task of store.mine(); track task.id) {
          <ng-container *ngTemplateOutlet="card; context: { $implicit: task }" />
        } @empty {
          <p class="neo-muted neo-body-small">{{ 'operator.tasks.mineEmpty' | translate }}</p>
        }

        @if (store.pool().length > 0) {
          <h2 class="op-section">{{ 'operator.tasks.pool' | translate }}</h2>
          @for (task of store.pool(); track task.id) {
            <ng-container *ngTemplateOutlet="card; context: { $implicit: task }" />
          }
        }
      }
    </div>

    <ng-template #card let-task>
      <article class="neo-card op-card" [attr.data-task]="task.id">
        <div class="op-card__head">
          <span class="op-card__name">
            {{ 'operator.taskType.' + task.type | translate }}
            <a class="op-card__sub" [routerLink]="['/operator/kiosks', task.kioskId]">
              {{ task.kioskCode }} — {{ task.kioskName }}
            </a>
          </span>
          <app-status-chip [status]="task.priority" [text]="'operator.priority.' + task.priority | translate" />
        </div>

        <ul class="op-rows">
          @if (task.notes) {
            <li class="op-row">
              <mat-icon aria-hidden="true">description</mat-icon>
              <span>{{ task.notes }}</span>
            </li>
          }
          <li class="op-row op-row--muted">
            <mat-icon aria-hidden="true">history</mat-icon>
            <span>
              {{ 'operator.taskStatus.' + task.status | translate }} ·
              @if (task.dueAt) {
                {{ 'operator.tasks.due' | translate: { time: (task.dueAt | dateTime) } }}
              } @else {
                {{ 'operator.tasks.opened' | translate: { time: (task.createdAt | dateTime) } }}
              }
            </span>
          </li>
        </ul>

        @if (canComplete()) {
          <button
            mat-flat-button
            type="button"
            class="op-primary"
            [disabled]="busy() !== null || !store.canAct()"
            (click)="next(task)"
          >
            {{ nextKey(task) | translate }}
          </button>
        }
      </article>
    </ng-template>
  `,
  styleUrl: './operator.scss',
})
export class OperatorTasksComponent {
  private readonly api = inject(OperatorApiService);
  private readonly auth = inject(AuthService);
  private readonly actions = inject(OperatorActionsService);
  private readonly feedback = inject(NotifyService);

  readonly store = inject(OperatorTasksStore);
  readonly notify = inject(OperatorNotifyService);

  readonly canComplete = computed(() => this.auth.can(Permissions.serviceTasksComplete));

  /** The task a step is being sent for. */
  readonly busy = signal<string | null>(null);

  constructor() {
    this.store.watch();
  }

  /** The words on a task's one button: always its next step. */
  nextKey(task: ServiceTask): string {
    switch (task.status) {
      case 'Open':
        return 'operator.tasks.accept';
      case 'Assigned':
        return 'operator.tasks.start';
      default:
        return 'operator.tasks.complete';
    }
  }

  async next(task: ServiceTask): Promise<void> {
    this.busy.set(task.id);

    try {
      if (task.status === 'InProgress') {
        const done = await this.actions.complete(task);

        if (done) {
          this.store.apply(done);
        }

        return;
      }

      const moved = await firstValueFrom(task.status === 'Open' ? this.api.accept(task.id) : this.api.start(task.id));

      this.store.apply(moved);
    } catch (error) {
      this.feedback.error(toProblem(error).message);
      void this.store.reload();
    } finally {
      this.busy.set(null);
    }
  }
}
