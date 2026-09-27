import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { TranslatePipe } from '@ngx-translate/core';

import { StorageSyncJob } from '../../core/api/step2.models';
import { BytesPipe, CountPipe, DateTimePipe } from '../../shared/format.pipes';
import { LiveIndicatorComponent } from '../../shared/live-indicator.component';
import { StatusChipComponent } from '../../shared/status-chip.component';

/**
 * A running sync job, live (plan section 2.10; docs/DESIGN_SYSTEM.md, "Uzun
 * əməliyyatın proqresi"): progress, counts, speed, time left, the stage in
 * words, and the controls that fit the job's state.
 */
@Component({
  selector: 'app-sync-progress',
  imports: [
    MatButtonModule,
    MatIconModule,
    MatProgressBarModule,
    TranslatePipe,
    LiveIndicatorComponent,
    StatusChipComponent,
    BytesPipe,
    CountPipe,
    DateTimePipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let current = job();
    <section
      class="neo-card progress"
      aria-live="polite"
      [attr.aria-label]="'storage.progress.label' | translate"
    >
      <div class="progress__header">
        <div>
          <h2>
            {{ 'storage.sync.mode.' + current.mode | translate }}: {{ current.sourceName }} →
            {{ current.targetName }}
          </h2>
          <p class="neo-caption">
            {{ 'storage.progress.startedAt' | translate: { time: (current.startedAt | dateTime) } }}
          </p>
        </div>
        <div class="progress__status">
          <app-status-chip
            [status]="current.status"
            [text]="'storage.sync.status.' + current.status | translate"
          />
          <app-live-indicator />
        </div>
      </div>

      <p class="progress__stage">{{ 'storage.progress.stage.' + current.status | translate }}</p>

      <mat-progress-bar
        [mode]="
          current.status === 'Pending' || current.totalObjects === 0
            ? 'indeterminate'
            : 'determinate'
        "
        [value]="percent()"
        [attr.aria-valuenow]="percent()"
      />

      <dl class="progress__figures neo-numeric">
        <div>
          <dt>{{ 'storage.progress.percent' | translate }}</dt>
          <dd>{{ percent() }}%</dd>
        </div>
        <div>
          <dt>{{ 'storage.progress.objects' | translate }}</dt>
          <dd>{{ current.copiedObjects | count }} / {{ current.totalObjects | count }}</dd>
        </div>
        <div>
          <dt>{{ 'storage.progress.size' | translate }}</dt>
          <dd>{{ current.copiedBytes | bytes }} / {{ current.totalBytes | bytes }}</dd>
        </div>
        <div>
          <dt>{{ 'storage.progress.speed' | translate }}</dt>
          <dd>{{ bytesPerSecond() === null ? '—' : (bytesPerSecond()! | bytes) + '/s' }}</dd>
        </div>
        <div>
          <dt>{{ 'storage.progress.remaining' | translate }}</dt>
          <dd>{{ remainingText() | translate: remainingParams() }}</dd>
        </div>
        @if (current.failedObjects > 0) {
          <div>
            <dt>{{ 'storage.progress.failed' | translate }}</dt>
            <dd class="progress__failed">{{ current.failedObjects | count }}</dd>
          </div>
        }
      </dl>

      <div class="neo-actions">
        @if (current.status === 'Running' || current.status === 'Pending') {
          <button
            mat-stroked-button
            type="button"
            [disabled]="busy()"
            (click)="pauseRequested.emit()"
          >
            <mat-icon>pause</mat-icon>
            {{ 'storage.progress.pause' | translate }}
          </button>
        }
        @if (current.status === 'Paused') {
          <button
            mat-flat-button
            type="button"
            [disabled]="busy()"
            (click)="resumeRequested.emit()"
          >
            <mat-icon>play_arrow</mat-icon>
            {{ 'storage.progress.resume' | translate }}
          </button>
        }
        @if (current.failedObjects > 0) {
          <button mat-button type="button" (click)="failuresRequested.emit()">
            {{ 'storage.progress.showFailures' | translate }}
          </button>
        }
        @if (current.status !== 'Verifying') {
          <button
            mat-button
            type="button"
            class="progress__cancel"
            [disabled]="busy()"
            (click)="cancelRequested.emit()"
          >
            <mat-icon>cancel</mat-icon>
            {{ 'storage.progress.cancel' | translate }}
          </button>
        }
      </div>
    </section>
  `,
  styles: `
    .progress {
      display: flex;
      flex-direction: column;
      gap: var(--space-3);
      border-color: var(--brand-300);
    }

    .progress__header {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-3);
      align-items: flex-start;
      justify-content: space-between;
    }

    .progress__status {
      display: flex;
      gap: var(--space-2);
      align-items: center;
    }

    .progress__stage {
      font-weight: var(--font-weight-medium);
    }

    .progress__figures {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
      gap: var(--space-3);
      margin: 0;

      dt {
        color: var(--neutral-600);
        font-size: var(--font-size-caption);
      }

      dd {
        margin: 0;
        font-weight: var(--font-weight-medium);
      }
    }

    .progress__failed {
      color: var(--danger-500);
    }

    .progress__cancel {
      color: var(--danger-500);
    }
  `,
})
export class SyncProgressComponent {
  readonly job = input.required<StorageSyncJob>();

  /** Bytes per second over the recent progress events, when known. */
  readonly bytesPerSecond = input<number | null>(null);

  readonly busy = input(false);

  readonly pauseRequested = output<void>();
  readonly resumeRequested = output<void>();
  readonly cancelRequested = output<void>();
  readonly failuresRequested = output<void>();

  readonly percent = computed(() => {
    const job = this.job();

    if (job.totalBytes > 0) {
      return Math.min(100, Math.floor((job.copiedBytes / job.totalBytes) * 100));
    }

    return job.totalObjects > 0
      ? Math.min(100, Math.floor((job.copiedObjects / job.totalObjects) * 100))
      : 0;
  });

  /** Seconds left at the current speed, when there is a speed to go by. */
  private readonly secondsLeft = computed(() => {
    const speed = this.bytesPerSecond();
    const job = this.job();

    if (!speed || speed <= 0 || job.status !== 'Running') {
      return null;
    }

    return Math.max(0, Math.round((job.totalBytes - job.copiedBytes) / speed));
  });

  readonly remainingText = computed(() => {
    const seconds = this.secondsLeft();

    if (seconds === null) {
      return 'storage.progress.remainingUnknown';
    }

    return seconds < 60 ? 'storage.progress.remainingSeconds' : 'storage.progress.remainingMinutes';
  });

  readonly remainingParams = computed(() => {
    const seconds = this.secondsLeft() ?? 0;

    return { seconds, minutes: Math.ceil(seconds / 60) };
  });
}
