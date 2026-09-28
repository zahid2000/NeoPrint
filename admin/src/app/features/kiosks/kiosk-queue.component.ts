import { Component, DestroyRef, OnInit, inject, input, signal } from '@angular/core';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService } from '../../core/api/admin-api.service';
import { QueueEntry } from '../../core/api/step3.models';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';

/**
 * Kiosk detail → Queue (plan section 8A.3): the queue the kiosk's own screen
 * shows, kept live by the QueueUpdated event.
 */
@Component({
  selector: 'app-kiosk-queue',
  imports: [MatTableModule, RouterLink, TranslatePipe, EmptyStateComponent, ErrorBannerComponent, StatusChipComponent, DateTimePipe],
  template: `
    <p class="neo-muted neo-body-small">{{ 'queue.tabDescription' | translate }}</p>

    @if (problem(); as problem) {
      <app-error-banner [problem]="problem" effect="queue.loadFailedEffect" (retry)="load()" />
    }

    @if (loading()) {
      <div class="neo-skeleton neo-skeleton--block"></div>
    } @else if (queue().length === 0) {
      <app-empty-state icon="queue" [message]="'queue.empty' | translate" [hint]="'queue.emptyHint' | translate" />
    } @else {
      <div class="neo-table-wrapper">
        <table mat-table class="neo-table" [dataSource]="queue()">
          <ng-container matColumnDef="position">
            <th mat-header-cell *matHeaderCellDef>{{ 'queue.colPosition' | translate }}</th>
            <td mat-cell *matCellDef="let entry">{{ entry.position }}</td>
          </ng-container>
          <ng-container matColumnDef="code">
            <th mat-header-cell *matHeaderCellDef>{{ 'queue.colCode' | translate }}</th>
            <td mat-cell *matCellDef="let entry">
              <a [routerLink]="['/print-jobs', entry.jobId]">
                <strong>{{ entry.jobCode }}</strong>
              </a>
            </td>
          </ng-container>
          <ng-container matColumnDef="status">
            <th mat-header-cell *matHeaderCellDef>{{ 'queue.colStatus' | translate }}</th>
            <td mat-cell *matCellDef="let entry">
              <app-status-chip [status]="entry.status" [text]="'jobs.status.' + entry.status | translate" />
            </td>
          </ng-container>
          <ng-container matColumnDef="pages">
            <th mat-header-cell *matHeaderCellDef>{{ 'queue.colPages' | translate }}</th>
            <td mat-cell *matCellDef="let entry">{{ entry.pages }} × {{ entry.copies }}</td>
          </ng-container>
          <ng-container matColumnDef="owner">
            <th mat-header-cell *matHeaderCellDef>{{ 'queue.colOwner' | translate }}</th>
            <td mat-cell *matCellDef="let entry">{{ entry.userInitial ?? ('queue.anonymous' | translate) }}</td>
          </ng-container>
          <ng-container matColumnDef="queued">
            <th mat-header-cell *matHeaderCellDef>{{ 'queue.colQueued' | translate }}</th>
            <td mat-cell *matCellDef="let entry">{{ entry.queuedAt | dateTime }}</td>
          </ng-container>

          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
        </table>
      </div>
    }
  `,
})
export class KioskQueueComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly hub = inject(AdminHubService);

  readonly kioskId = input.required<string>();

  readonly columns = ['position', 'code', 'status', 'pages', 'owner', 'queued'];

  readonly queue = signal<readonly QueueEntry[]>([]);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);

  constructor() {
    const off = this.hub.on<{ kioskId: string; queue: QueueEntry[] }>('QueueUpdated', (event) => {
      if (event.kioskId === this.kioskId()) {
        this.queue.set(event.queue);
      }
    });
    inject(DestroyRef).onDestroy(off);
  }

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.problem.set(null);

    try {
      this.queue.set(await firstValueFrom(this.api.kioskQueue(this.kioskId())));
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }
}
