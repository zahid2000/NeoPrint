import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom, map } from 'rxjs';

import { IncidentRow } from '../../core/api/step4.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { HubEvents } from '../../core/realtime/hub-events';
import { confirmAction } from '../../shared/confirm.dialog';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { OperatorActionsService } from './operator-actions.service';
import { OperatorApiService } from './operator-api.service';
import { OperatorFreshnessComponent } from './operator-freshness.component';
import { operatorScreen } from './operator-screen';

/**
 * "Incident-lər" (plan section 8B): what is open at the operator's kiosks, the
 * most severe first. "Gördüm, baxıram" tells everybody somebody is on it;
 * closing one takes a note of what was done.
 */
@Component({
  selector: 'app-operator-incidents',
  imports: [
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
      <h1 class="op-title">{{ 'operator.incidents.title' | translate }}</h1>

      @if (screen.problem(); as problem) {
        <app-error-banner [problem]="problem" effect="operator.loadFailedEffect" (retry)="screen.reload()" />
      } @else if (screen.loading()) {
        @for (row of [1, 2]; track row) {
          <div class="neo-skeleton neo-skeleton--block"></div>
        }
      } @else if (incidents().length === 0) {
        <app-empty-state
          icon="check_circle"
          [message]="'operator.incidents.empty' | translate"
          [hint]="'operator.incidents.emptyHint' | translate"
        />
      } @else {
        <app-operator-freshness [at]="screen.at()" [offline]="screen.offline()" />

        @for (incident of incidents(); track incident.id) {
          <article class="neo-card op-card" [attr.data-incident]="incident.id">
            <div class="op-card__head">
              <span class="op-card__name">
                @if (incident.kioskId) {
                  <a [routerLink]="['/operator/kiosks', incident.kioskId]">{{ incident.kioskCode }}</a>
                }
                <span class="op-card__sub">{{ incident.createdAt | dateTime }}</span>
              </span>
              <app-status-chip
                [status]="incident.severity"
                [text]="'money.incidentSeverity.' + incident.severity | translate"
              />
            </div>

            <p class="op-row">
              <mat-icon aria-hidden="true">report</mat-icon>
              <span>{{ incident.details }}</span>
            </p>

            <div class="op-card__actions">
              @if (incident.status === 'Acknowledged') {
                <app-status-chip status="Acknowledged" [text]="'operator.incidents.acknowledged' | translate" />
              } @else if (canAcknowledge()) {
                <button
                  mat-stroked-button
                  type="button"
                  [disabled]="busy() !== null || !screen.canAct()"
                  (click)="acknowledge(incident)"
                >
                  {{ 'operator.incidents.acknowledge' | translate }}
                </button>
              }
              @if (canResolve()) {
                <button
                  mat-flat-button
                  type="button"
                  [disabled]="busy() !== null || !screen.canAct()"
                  (click)="resolve(incident)"
                >
                  {{ 'operator.incidents.resolve' | translate }}
                </button>
              }
            </div>
          </article>
        }
      }
    </div>
  `,
  styleUrl: './operator.scss',
})
export class OperatorIncidentsComponent {
  private readonly api = inject(OperatorApiService);
  private readonly auth = inject(AuthService);
  private readonly actions = inject(OperatorActionsService);
  private readonly dialog = inject(MatDialog);
  private readonly translate = inject(TranslateService);

  readonly canAcknowledge = computed(() => this.auth.can(Permissions.incidentsAcknowledge));
  readonly canResolve = computed(() => this.auth.can(Permissions.incidentsResolve));

  /** The incident something is being sent for. */
  readonly busy = signal<string | null>(null);

  readonly screen = operatorScreen<readonly IncidentRow[]>({
    key: () => 'incidents',
    fetch: () => this.api.incidents().pipe(map((page) => page.items)),
    events: [HubEvents.incidentRaised, HubEvents.incidentResolved],
  });

  readonly incidents = computed(() => this.screen.data() ?? []);

  constructor() {
    void this.screen.reload();
  }

  async acknowledge(incident: IncidentRow): Promise<void> {
    await this.run(incident, () => firstValueFrom(this.api.acknowledge(incident.id)));
  }

  /** Closing takes a note: what was done goes to the audit log (plan section 8A.1). */
  async resolve(incident: IncidentRow): Promise<void> {
    const text = (key: string): string => this.translate.instant(key) as string;

    const confirmed = await confirmAction(this.dialog, {
      title: text('operator.incidents.resolveTitle'),
      explanation: text('operator.incidents.resolveExplanation'),
      confirmLabel: text('operator.incidents.resolve'),
      cancelLabel: text('common.cancel'),
      reasonLabel: text('operator.incidents.resolveNote'),
      requireReason: true,
    });

    if (confirmed) {
      await this.run(incident, () => firstValueFrom(this.api.resolve(incident.id, confirmed.reason)));
    }
  }

  private async run(incident: IncidentRow, action: () => Promise<unknown>): Promise<void> {
    this.busy.set(incident.id);

    try {
      await this.actions.send(action);
      await this.screen.reload();
    } finally {
      this.busy.set(null);
    }
  }
}
