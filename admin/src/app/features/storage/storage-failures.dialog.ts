import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule } from '@angular/material/dialog';
import { MatTableModule } from '@angular/material/table';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService } from '../../core/api/admin-api.service';
import { StorageSyncFailure, StorageSyncJob } from '../../core/api/step2.models';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';

/** The objects a sync job could not copy, and whether each was fixed later. */
@Component({
  selector: 'app-storage-failures-dialog',
  imports: [
    MatButtonModule,
    MatDialogModule,
    MatTableModule,
    TranslatePipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    StatusChipComponent,
    DateTimePipe,
  ],
  template: `
    <h2 mat-dialog-title>{{ 'storage.failures.title' | translate }}</h2>

    <mat-dialog-content class="neo-dialog">
      <p class="neo-dialog__explanation">
        {{
          'storage.failures.explanation'
            | translate: { source: job.sourceName, target: job.targetName }
        }}
      </p>

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" (retry)="load()" />
      } @else if (loading()) {
        @for (row of [1, 2, 3]; track row) {
          <div class="neo-skeleton neo-skeleton--row"></div>
        }
      } @else if (failures().length === 0) {
        <app-empty-state icon="check_circle" [message]="'storage.failures.none' | translate" />
      } @else {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="failures()">
            <ng-container matColumnDef="object">
              <th mat-header-cell *matHeaderCellDef>{{ 'storage.failures.object' | translate }}</th>
              <td mat-cell *matCellDef="let failure">
                <code>{{ failure.bucket }}/{{ failure.key }}</code>
              </td>
            </ng-container>
            <ng-container matColumnDef="error">
              <th mat-header-cell *matHeaderCellDef>{{ 'storage.failures.error' | translate }}</th>
              <td mat-cell *matCellDef="let failure">{{ failure.error }}</td>
            </ng-container>
            <ng-container matColumnDef="attempts">
              <th mat-header-cell *matHeaderCellDef>
                {{ 'storage.failures.attempts' | translate }}
              </th>
              <td mat-cell *matCellDef="let failure">{{ failure.attempts }}</td>
            </ng-container>
            <ng-container matColumnDef="state">
              <th mat-header-cell *matHeaderCellDef>{{ 'storage.failures.state' | translate }}</th>
              <td mat-cell *matCellDef="let failure">
                @if (failure.resolvedAt) {
                  <app-status-chip
                    status="Completed"
                    [text]="'storage.failures.resolved' | translate"
                  />
                } @else {
                  <app-status-chip status="Failed" [text]="'storage.failures.open' | translate" />
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="at">
              <th mat-header-cell *matHeaderCellDef>
                {{ 'storage.failures.lastAttempt' | translate }}
              </th>
              <td mat-cell *matCellDef="let failure">{{ failure.lastAttemptAt | dateTime }}</td>
            </ng-container>

            <tr mat-header-row *matHeaderRowDef="columns"></tr>
            <tr mat-row *matRowDef="let row; columns: columns"></tr>
          </table>
        </div>
      }
    </mat-dialog-content>

    <mat-dialog-actions class="neo-dialog__actions">
      <button mat-flat-button type="button" mat-dialog-close>
        {{ 'common.close' | translate }}
      </button>
    </mat-dialog-actions>
  `,
})
export class StorageFailuresDialogComponent {
  private readonly api = inject(AdminApiService);

  readonly job = inject<StorageSyncJob>(MAT_DIALOG_DATA);

  readonly columns = ['object', 'error', 'attempts', 'state', 'at'];

  readonly failures = signal<StorageSyncFailure[]>([]);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.problem.set(null);

    try {
      this.failures.set(await firstValueFrom(this.api.storageSyncFailures(this.job.id)));
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }
}
