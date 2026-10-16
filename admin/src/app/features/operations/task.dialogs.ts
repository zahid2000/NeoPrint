import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { KioskListItem } from '../../core/api/admin-api.service';
import { OperationsApiService } from '../../core/api/operations-api.service';
import { ServiceTaskAssignee, ServiceTaskDetail } from '../../core/api/step7.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { confirmAction } from '../../shared/confirm.dialog';
import { provideNeoDates } from '../../shared/date-input';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { taskStatusTones } from '../kiosks/kiosk-tasks.component';

export const taskTypes = ['RefillPaper', 'ReplaceToner', 'ReplaceDrum', 'PrinterRepair', 'Cleaning', 'Other'] as const;
export const taskPriorities = ['Low', 'Normal', 'High', 'Critical'] as const;

/** The end of the chosen day: a task due "on the 20th" is due until that evening. */
function dueAt(day: Date | null): string | null {
  if (!day) {
    return null;
  }

  const end = new Date(day.getFullYear(), day.getMonth(), day.getDate(), 23, 59, 0);

  return end.toISOString();
}

/** Who may take it: operators first, each with what they already have. */
function assigneeLabel(translate: TranslateService, person: ServiceTaskAssignee): string {
  return translate.instant(person.operator ? 'ops.tasks.assigneeOperator' : 'ops.tasks.assigneeManager', {
    name: person.fullName,
    count: person.activeTasks,
  }) as string;
}

export interface NewTaskData {
  readonly kiosks: readonly KioskListItem[];
}

/**
 * "Yeni tapşırıq" (plan section 2.15): which kiosk, what work, how urgent, by
 * when and for whom. Left without somebody, the system gives it to the least
 * busy operator of the kiosk, or puts it in the pool.
 */
@Component({
  selector: 'app-new-task-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatDatepickerModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    TranslatePipe,
    ErrorBannerComponent,
  ],
  providers: [provideNeoDates()],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ 'ops.tasks.newTitle' | translate }}</h2>
    <mat-dialog-content class="neo-dialog">
      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="ops.tasks.saveFailedEffect" />
      }
      <div class="ops__form">
        <mat-form-field class="ops__wide">
          <mat-label>{{ 'ops.kiosk' | translate }}</mat-label>
          <mat-select [ngModel]="kioskId()" (ngModelChange)="chooseKiosk($event)">
            @for (kiosk of data.kiosks; track kiosk.id) {
              <mat-option [value]="kiosk.id">{{ kiosk.code }} — {{ kiosk.name }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'ops.tasks.type' | translate }}</mat-label>
          <mat-select [ngModel]="type()" (ngModelChange)="type.set($event)">
            @for (option of types; track option) {
              <mat-option [value]="option">{{ 'operator.taskType.' + option | translate }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'monitoring.tasks.colPriority' | translate }}</mat-label>
          <mat-select [ngModel]="priority()" (ngModelChange)="priority.set($event)">
            @for (option of priorities; track option) {
              <mat-option [value]="option">{{ 'operator.priority.' + option | translate }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'ops.tasks.due' | translate }}</mat-label>
          <input matInput [matDatepicker]="picker" [min]="today" [placeholder]="'date.hint' | translate" [ngModel]="due()" (ngModelChange)="due.set($event)" />
          <mat-datepicker-toggle matIconSuffix [for]="picker" />
          <mat-datepicker #picker />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'monitoring.tasks.colAssignee' | translate }}</mat-label>
          <mat-select [ngModel]="assignTo()" (ngModelChange)="assignTo.set($event)" [disabled]="!kioskId()">
            <mat-option [value]="null">{{ 'ops.tasks.assignAuto' | translate }}</mat-option>
            @for (person of assignees(); track person.id) {
              <mat-option [value]="person.id">{{ label(person) }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field class="ops__wide">
          <mat-label>{{ 'ops.tasks.notes' | translate }}</mat-label>
          <textarea matInput rows="3" maxlength="500" [ngModel]="notes()" (ngModelChange)="notes.set($event)"></textarea>
        </mat-form-field>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions class="neo-dialog__actions">
      <button mat-button type="button" [disabled]="busy()" (click)="ref.close(false)">{{ 'common.cancel' | translate }}</button>
      <button mat-flat-button type="button" [disabled]="!kioskId() || busy()" (click)="save()">{{ 'ops.tasks.create' | translate }}</button>
    </mat-dialog-actions>
  `,
  styleUrl: './operations.scss',
})
export class NewTaskDialogComponent {
  readonly data = inject<NewTaskData>(MAT_DIALOG_DATA);
  readonly ref = inject<MatDialogRef<NewTaskDialogComponent, boolean>>(MatDialogRef);
  private readonly api = inject(OperationsApiService);
  private readonly translate = inject(TranslateService);

  readonly types = taskTypes;
  readonly priorities = taskPriorities;
  readonly today = new Date();

  readonly kioskId = signal<string | null>(null);
  readonly type = signal<string>('Cleaning');
  readonly priority = signal<string>('Normal');
  readonly due = signal<Date | null>(null);
  readonly assignTo = signal<string | null>(null);
  readonly notes = signal('');
  readonly assignees = signal<readonly ServiceTaskAssignee[]>([]);
  readonly busy = signal(false);
  readonly problem = signal<Problem | null>(null);

  label(person: ServiceTaskAssignee): string {
    return assigneeLabel(this.translate, person);
  }

  async chooseKiosk(kioskId: string): Promise<void> {
    this.kioskId.set(kioskId);
    this.assignTo.set(null);
    this.assignees.set([]);

    try {
      this.assignees.set(await firstValueFrom(this.api.assignees(kioskId)));
    } catch {
      // Without the list the system still picks somebody.
    }
  }

  async save(): Promise<void> {
    const kioskId = this.kioskId();

    if (!kioskId) {
      return;
    }

    this.busy.set(true);
    this.problem.set(null);

    try {
      await firstValueFrom(
        this.api.createTask({
          kioskId,
          type: this.type(),
          priority: this.priority(),
          notes: this.notes().trim() || null,
          dueAt: dueAt(this.due()),
          assignTo: this.assignTo(),
        }),
      );
      this.ref.close(true);
    } catch (error) {
      this.problem.set(toProblem(error));
      this.busy.set(false);
    }
  }
}

/**
 * A task's detail (plan section 8A.3, "Servis tapşırıqları"): what happened
 * to it and when, and - while it is still to be done - who has it, how urgent
 * it is and by when, or that it is called off.
 */
@Component({
  selector: 'app-task-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatDatepickerModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    TranslatePipe,
    DateTimePipe,
    ErrorBannerComponent,
    StatusChipComponent,
  ],
  providers: [provideNeoDates()],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>
      {{ 'operator.taskType.' + task().type | translate }} · {{ task().kioskCode }}
    </h2>
    <mat-dialog-content class="neo-dialog">
      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="ops.tasks.saveFailedEffect" />
      }
      <p>
        <app-status-chip [status]="task().priority" [text]="'operator.priority.' + task().priority | translate" />
        <app-status-chip
          [status]="task().status"
          [toneOverride]="tones[task().status]"
          [text]="'operator.taskStatus.' + task().status | translate"
        />
      </p>
      @if (task().notes) {
        <p>{{ task().notes }}</p>
      }

      <h3>{{ 'ops.tasks.history' | translate }}</h3>
      <ul class="ops__timeline">
        <li>
          <span class="ops__when">{{ task().createdAt | dateTime }}</span>
          <span>{{ 'ops.tasks.step.created' | translate }} · {{ 'ops.tasks.source.' + task().source | translate }}</span>
        </li>
        @if (task().assignedAt) {
          <li>
            <span class="ops__when">{{ task().assignedAt | dateTime }}</span>
            <span>{{ 'ops.tasks.step.assigned' | translate: { name: task().assignedToName ?? '' } }}</span>
          </li>
        }
        @if (task().escalatedAt) {
          <li>
            <span class="ops__when">{{ task().escalatedAt | dateTime }}</span>
            <span>{{ 'ops.tasks.step.escalated' | translate }}</span>
          </li>
        }
        @if (task().startedAt) {
          <li>
            <span class="ops__when">{{ task().startedAt | dateTime }}</span>
            <span>{{ 'ops.tasks.step.started' | translate }}</span>
          </li>
        }
        @if (task().completedAt) {
          <li>
            <span class="ops__when">{{ task().completedAt | dateTime }}</span>
            <span>
              {{ (task().status === 'Cancelled' ? 'ops.tasks.step.cancelled' : 'ops.tasks.step.done') | translate }}
              @if (task().cancelReason) {
                <span class="neo-caption ops__block">{{ task().cancelReason }}</span>
              }
              @if (task().completion?.sheetsAdded) {
                <span class="neo-caption ops__block">{{ 'ops.tasks.sheetsAdded' | translate: { sheets: task().completion?.sheetsAdded } }}</span>
              }
              @if (task().completion?.note) {
                <span class="neo-caption ops__block">{{ task().completion?.note }}</span>
              }
            </span>
          </li>
        }
      </ul>

      @if (active() && canManage()) {
        <h3>{{ 'ops.tasks.change' | translate }}</h3>
        <div class="ops__form">
          @if (task().status !== 'InProgress') {
            <mat-form-field class="ops__wide">
              <mat-label>{{ 'monitoring.tasks.colAssignee' | translate }}</mat-label>
              <mat-select [ngModel]="assignTo()" (ngModelChange)="assignTo.set($event)">
                <mat-option [value]="null">{{ 'ops.tasks.toPool' | translate }}</mat-option>
                @for (person of assignees(); track person.id) {
                  <mat-option [value]="person.id">{{ label(person) }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
          }
          <mat-form-field>
            <mat-label>{{ 'monitoring.tasks.colPriority' | translate }}</mat-label>
            <mat-select [ngModel]="priority()" (ngModelChange)="priority.set($event)">
              @for (option of priorities; track option) {
                <mat-option [value]="option">{{ 'operator.priority.' + option | translate }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'ops.tasks.due' | translate }}</mat-label>
            <input matInput [matDatepicker]="picker" [min]="today" [placeholder]="'date.hint' | translate" [ngModel]="due()" (ngModelChange)="due.set($event)" />
            <mat-datepicker-toggle matIconSuffix [for]="picker" />
            <mat-datepicker #picker />
          </mat-form-field>
          <mat-form-field class="ops__wide">
            <mat-label>{{ 'ops.tasks.notes' | translate }}</mat-label>
            <textarea matInput rows="2" maxlength="500" [ngModel]="notes()" (ngModelChange)="notes.set($event)"></textarea>
          </mat-form-field>
        </div>
      }
    </mat-dialog-content>
    <mat-dialog-actions class="neo-dialog__actions">
      @if (active() && canManage()) {
        <button mat-button type="button" class="ops__danger" [disabled]="busy()" (click)="cancelTask()">{{ 'ops.tasks.cancel' | translate }}</button>
      }
      <button mat-button type="button" [disabled]="busy()" (click)="ref.close(changed)">{{ 'common.close' | translate }}</button>
      @if (active() && canManage()) {
        <button mat-flat-button type="button" [disabled]="busy()" (click)="save()">{{ 'common.save' | translate }}</button>
      }
    </mat-dialog-actions>
  `,
  styles: `
    .ops__danger:not(:disabled) {
      color: var(--danger-500);
    }

    p app-status-chip + app-status-chip {
      margin-inline-start: var(--space-2);
    }
  `,
  styleUrl: './operations.scss',
})
export class TaskDialogComponent implements OnInit {
  private readonly data = inject<{ task: ServiceTaskDetail }>(MAT_DIALOG_DATA);
  readonly ref = inject<MatDialogRef<TaskDialogComponent, boolean>>(MatDialogRef);
  private readonly api = inject(OperationsApiService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly translate = inject(TranslateService);

  readonly priorities = taskPriorities;
  readonly tones = taskStatusTones;
  readonly today = new Date();

  readonly task = signal<ServiceTaskDetail>(this.data.task);
  readonly assignTo = signal<string | null>(this.data.task.assignedTo ?? null);
  readonly priority = signal<string>(this.data.task.priority);
  readonly due = signal<Date | null>(this.data.task.dueAt ? new Date(this.data.task.dueAt) : null);
  readonly notes = signal(this.data.task.notes ?? '');
  readonly assignees = signal<readonly ServiceTaskAssignee[]>([]);
  readonly busy = signal(false);
  readonly problem = signal<Problem | null>(null);

  readonly canManage = computed(() => this.auth.can(Permissions.serviceTasksManage));
  readonly active = computed(() => ['Open', 'Assigned', 'InProgress'].includes(this.task().status));

  /** Whether anything was saved: the list asks again when the dialog closes. */
  changed = false;

  async ngOnInit(): Promise<void> {
    if (this.active() && this.canManage()) {
      try {
        this.assignees.set(await firstValueFrom(this.api.assignees(this.task().kioskId)));
      } catch {
        // The current holder stays chosen; another cannot be offered.
      }
    }
  }

  label(person: ServiceTaskAssignee): string {
    return assigneeLabel(this.translate, person);
  }

  async save(): Promise<void> {
    await this.run(async () => {
      let task = this.task();

      if (task.status !== 'InProgress' && (this.assignTo() ?? null) !== (task.assignedTo ?? null)) {
        task = await firstValueFrom(this.api.assignTask(task.id, this.assignTo()));
      }

      const due = dueAt(this.due());
      const notes = this.notes().trim() || null;

      if (this.priority() !== task.priority || notes !== (task.notes ?? null) || due !== (task.dueAt ?? null)) {
        task = await firstValueFrom(this.api.updateTask(task.id, { priority: this.priority(), notes, dueAt: due }));
      }

      this.task.set(task);
      this.changed = true;
      this.ref.close(true);
    });
  }

  async cancelTask(): Promise<void> {
    const text = (key: string): string => this.translate.instant(key) as string;
    const answer = await confirmAction(this.dialog, {
      title: text('ops.tasks.cancelTitle'),
      explanation: text('ops.tasks.cancelExplanation'),
      confirmLabel: text('ops.tasks.cancel'),
      cancelLabel: text('common.back'),
      destructive: true,
      reasonLabel: text('ops.tasks.cancelReason'),
    });

    if (answer === undefined) {
      return;
    }

    await this.run(async () => {
      this.task.set(await firstValueFrom(this.api.cancelTask(this.task().id, answer.reason)));
      this.changed = true;
      this.ref.close(true);
    });
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);

    try {
      await work();
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.busy.set(false);
    }
  }
}
