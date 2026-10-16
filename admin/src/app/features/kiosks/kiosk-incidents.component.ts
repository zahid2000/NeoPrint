import { Component, OnInit, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatTableModule } from '@angular/material/table';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MonitoringApiService } from '../../core/api/monitoring-api.service';
import { IncidentRow } from '../../core/api/step4.models';
import { HubEvents, IncidentEvent } from '../../core/realtime/hub-events';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent, StatusTone } from '../../shared/status-chip.component';

const statusTones: Readonly<Record<string, StatusTone>> = {
  Open: 'danger',
  Acknowledged: 'warning',
  Resolved: 'success',
};

/**
 * Kiosk detail → "Incident-lər" (plan sections 9.6, 8A.3): what went wrong
 * at this kiosk and whether it is over - live, as incidents open and close.
 * Acknowledging and closing them is the "Incident-lər" page's work.
 */
@Component({
  selector: 'app-kiosk-incidents',
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
      <p class="neo-muted neo-body-small">{{ 'monitoring.incidents.description' | translate }}</p>
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
      <app-error-banner [problem]="problem" effect="monitoring.incidents.loadFailedEffect" (retry)="load()" />
    } @else if (incidents() === null) {
      <div class="neo-skeleton neo-skeleton--block"></div>
    } @else if (incidents()!.length === 0) {
      <app-empty-state
        [icon]="openOnly() ? 'verified' : 'inbox'"
        [message]="(openOnly() ? 'monitoring.incidents.noneOpen' : 'monitoring.incidents.none') | translate"
      />
    } @else {
      <div class="neo-table-wrapper">
        <table mat-table class="neo-table" [dataSource]="incidents()!">
          <ng-container matColumnDef="severity">
            <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.incidents.colSeverity' | translate }}</th>
            <td mat-cell *matCellDef="let incident">
              <app-status-chip
                [status]="incident.severity"
                [text]="'money.incidentSeverity.' + incident.severity | translate"
              />
            </td>
          </ng-container>
          <ng-container matColumnDef="details">
            <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.incidents.colDetails' | translate }}</th>
            <td mat-cell *matCellDef="let incident">
              {{ incident.details }}
              @if (incident.resolution) {
                <span class="neo-caption tab__block">{{ incident.resolution }}</span>
              }
            </td>
          </ng-container>
          <ng-container matColumnDef="status">
            <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.incidents.colStatus' | translate }}</th>
            <td mat-cell *matCellDef="let incident">
              <app-status-chip
                [status]="incident.status"
                [toneOverride]="tones[incident.status]"
                [text]="'monitoring.incidentStatus.' + incident.status | translate"
              />
            </td>
          </ng-container>
          <ng-container matColumnDef="opened">
            <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.incidents.colOpened' | translate }}</th>
            <td mat-cell *matCellDef="let incident" class="neo-nowrap">{{ incident.createdAt | dateTime }}</td>
          </ng-container>
          <ng-container matColumnDef="closed">
            <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.incidents.colClosed' | translate }}</th>
            <td mat-cell *matCellDef="let incident" class="neo-nowrap">{{ incident.resolvedAt | dateTime }}</td>
          </ng-container>

          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
        </table>
      </div>
    }
  `,
  styleUrl: './kiosk-tab.scss',
})
export class KioskIncidentsComponent implements OnInit {
  private readonly api = inject(MonitoringApiService);

  readonly kioskId = input.required<string>();

  readonly columns = ['severity', 'details', 'status', 'opened', 'closed'];
  readonly tones = statusTones;

  readonly openOnly = signal(true);
  readonly incidents = signal<readonly IncidentRow[] | null>(null);
  readonly problem = signal<Problem | null>(null);

  constructor() {
    liveRefresh({
      events: [HubEvents.incidentRaised, HubEvents.incidentResolved],
      accepts: (_event, payload) => (payload as IncidentEvent | null)?.kioskId === this.kioskId(),
      reload: () => this.load(true),
    });
  }

  ngOnInit(): void {
    void this.load();
  }

  setOpenOnly(openOnly: boolean): void {
    this.openOnly.set(openOnly);
    this.incidents.set(null);
    void this.load();
  }

  async load(quiet = false): Promise<void> {
    if (!quiet) {
      this.problem.set(null);
    }

    try {
      this.incidents.set((await firstValueFrom(this.api.kioskIncidents(this.kioskId(), this.openOnly()))).items);
    } catch (error) {
      if (!quiet) {
        this.problem.set(toProblem(error));
      }
    }
  }
}
