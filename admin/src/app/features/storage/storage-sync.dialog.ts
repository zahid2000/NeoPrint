import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatRadioModule } from '@angular/material/radio';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService } from '../../core/api/admin-api.service';
import { StorageProvider, StorageSyncJob, StorageSyncMode } from '../../core/api/step2.models';
import { toProblem } from '../../shared/problem';

export interface StorageSyncDialogData {
  readonly providers: readonly StorageProvider[];

  /** Pre-selected from a provider's menu ("Secondary et" opens a mirror). */
  readonly targetId?: string;
  readonly mode?: StorageSyncMode;
}

/**
 * The storage sync wizard (plan sections 2.10, 9.8): choose migration or
 * mirror, the target, and - for a migration - whether the target takes over
 * as primary when it is done; then confirm what will happen.
 *
 * The source is always the current primary, as the server decides.
 */
@Component({
  selector: 'app-storage-sync-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatCheckboxModule,
    MatDialogModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatRadioModule,
    TranslatePipe,
  ],
  template: `
    <h2 mat-dialog-title>{{ 'storage.sync.wizardTitle' | translate }}</h2>

    <mat-dialog-content class="neo-dialog">
      <ol class="steps" [attr.aria-label]="'storage.sync.wizardTitle' | translate">
        @for (label of stepLabels; track label; let index = $index) {
          <li [class.steps__current]="index === step()" [class.steps__done]="index < step()">
            <span class="steps__number">{{ index + 1 }}</span>
            {{ label | translate }}
          </li>
        }
      </ol>

      @switch (step()) {
        @case (0) {
          <mat-radio-group class="choices" [ngModel]="mode()" (ngModelChange)="mode.set($event)">
            <mat-radio-button value="Migration">
              <strong>{{ 'storage.sync.migration' | translate }}</strong>
              <span class="neo-caption choices__hint">{{ 'storage.sync.migrationHint' | translate }}</span>
            </mat-radio-button>
            <mat-radio-button value="Mirror">
              <strong>{{ 'storage.sync.mirror' | translate }}</strong>
              <span class="neo-caption choices__hint">{{ 'storage.sync.mirrorHint' | translate }}</span>
            </mat-radio-button>
          </mat-radio-group>
        }
        @case (1) {
          <p>{{ 'storage.sync.sourceIs' | translate: { name: primary()?.name ?? '—' } }}</p>

          @if (targets().length === 0) {
            <p class="neo-field-error" role="alert">
              <mat-icon aria-hidden="true">error</mat-icon>
              {{ 'storage.sync.noTargets' | translate }}
            </p>
          } @else {
            <mat-radio-group class="choices" [ngModel]="targetId()" (ngModelChange)="targetId.set($event)">
              @for (provider of targets(); track provider.id) {
                <mat-radio-button [value]="provider.id">
                  <strong>{{ provider.name }}</strong>
                  <span class="neo-caption choices__hint">{{ provider.type }} · {{ provider.endpoint }}</span>
                </mat-radio-button>
              }
            </mat-radio-group>
          }

          @if (mode() === 'Migration') {
            <mat-checkbox [ngModel]="switchPrimary()" (ngModelChange)="switchPrimary.set($event)">
              {{ 'storage.sync.switchPrimary' | translate }}
            </mat-checkbox>
          }
        }
        @case (2) {
          <p class="summary">
            {{
              (mode() === 'Migration'
                ? switchPrimary()
                  ? 'storage.sync.summaryMigrationSwitch'
                  : 'storage.sync.summaryMigration'
                : 'storage.sync.summaryMirror'
              ) | translate: { source: primary()?.name ?? '—', target: target()?.name ?? '—' }
            }}
          </p>
          <p class="neo-caption">{{ 'storage.sync.summaryNote' | translate }}</p>
        }
      }

      @if (error(); as message) {
        <p class="neo-field-error" role="alert">
          <mat-icon aria-hidden="true">error</mat-icon>
          {{ message | translate }}
        </p>
      }
    </mat-dialog-content>

    <mat-dialog-actions class="neo-dialog__actions">
      @if (step() === 0) {
        <button mat-button type="button" (click)="close()">{{ 'common.cancel' | translate }}</button>
      } @else {
        <button mat-button type="button" [disabled]="starting()" (click)="step.set(step() - 1)">
          {{ 'common.back' | translate }}
        </button>
      }

      @if (step() < 2) {
        <button mat-flat-button type="button" [disabled]="!canContinue()" (click)="step.set(step() + 1)">
          {{ 'common.next' | translate }}
        </button>
      } @else {
        <button mat-flat-button type="button" [disabled]="starting()" (click)="start()">
          @if (starting()) {
            <mat-spinner diameter="18" class="neo-button-spinner" />
          }
          {{ 'storage.sync.start' | translate }}
        </button>
      }
    </mat-dialog-actions>
  `,
  styles: `
    .steps {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-4);
      margin: 0;
      padding: 0;
      color: var(--neutral-600);
      font-size: var(--font-size-body-small);
      list-style: none;

      li {
        display: flex;
        gap: var(--space-2);
        align-items: center;
      }
    }

    .steps__number {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: var(--status-chip-height);
      height: var(--status-chip-height);
      border: 1px solid var(--neutral-400);
      border-radius: var(--radius-full);
    }

    .steps__current {
      color: var(--brand-600);
      font-weight: var(--font-weight-semibold);

      .steps__number {
        border-color: var(--brand-500);
        background: var(--brand-500);
        color: var(--neutral-0);
      }
    }

    .steps__done .steps__number {
      border-color: var(--success-500);
      color: var(--success-500);
    }

    .choices {
      display: flex;
      flex-direction: column;
      gap: var(--space-3);
    }

    .choices__hint {
      display: block;
    }

    .summary {
      padding: var(--space-3) var(--space-4);
      border-radius: var(--radius-sm);
      background: var(--info-bg);
      color: var(--neutral-800);
    }
  `,
})
export class StorageSyncDialogComponent {
  private readonly api = inject(AdminApiService);
  private readonly dialogRef =
    inject<MatDialogRef<StorageSyncDialogComponent, StorageSyncJob | undefined>>(MatDialogRef);
  private readonly data = inject<StorageSyncDialogData>(MAT_DIALOG_DATA);

  readonly stepLabels = ['storage.sync.stepMode', 'storage.sync.stepTarget', 'storage.sync.stepConfirm'];

  readonly step = signal(this.data.targetId ? 1 : 0);
  readonly mode = signal<StorageSyncMode>(this.data.mode ?? 'Migration');
  readonly targetId = signal<string | null>(this.data.targetId ?? null);
  readonly switchPrimary = signal(true);
  readonly starting = signal(false);
  readonly error = signal<string | null>(null);

  readonly primary = computed(() => this.data.providers.find((provider) => provider.role === 'Primary') ?? null);

  /** A target has to be tested and must not be the primary itself. */
  readonly targets = computed(() =>
    this.data.providers.filter((provider) => provider.role !== 'Primary' && provider.isVerified),
  );

  readonly target = computed(() => this.targets().find((provider) => provider.id === this.targetId()) ?? null);

  readonly canContinue = computed(() => (this.step() === 0 ? true : this.target() !== null));

  async start(): Promise<void> {
    const target = this.target();

    if (!target) {
      return;
    }

    this.starting.set(true);
    this.error.set(null);

    try {
      this.dialogRef.close(
        await firstValueFrom(
          this.api.startStorageSync({
            mode: this.mode(),
            targetId: target.id,
            switchPrimaryOnComplete: this.mode() === 'Migration' && this.switchPrimary(),
          }),
        ),
      );
    } catch (error) {
      this.error.set(toProblem(error).message);
    } finally {
      this.starting.set(false);
    }
  }

  close(): void {
    this.dialogRef.close(undefined);
  }
}
