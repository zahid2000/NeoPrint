import { Component, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';

import { KioskStatus } from '../../core/api/admin-api.service';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { HubEvents } from '../../core/realtime/hub-events';
import { KioskLiveStore } from '../../core/realtime/kiosk-live.store';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { OperatorActionsService } from './operator-actions.service';
import { OperatorApiService } from './operator-api.service';
import { OperatorFreshnessComponent } from './operator-freshness.component';
import { OperatorKioskCardComponent } from './operator-kiosk-card.component';
import { OperatorKioskCard } from './operator.models';
import { operatorScreen } from './operator-screen';

/**
 * "Mənim kiosklarım" (plan section 8B): a health card for each kiosk the
 * operator looks after, the ones that need somebody first. A card opens the
 * kiosk; its one button is the thing an operator does most - a refill.
 */
@Component({
  selector: 'app-operator-kiosks',
  imports: [
    RouterLink,
    MatButtonModule,
    MatIconModule,
    TranslatePipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    OperatorFreshnessComponent,
    OperatorKioskCardComponent,
  ],
  template: `
    <div class="op-page">
      <div class="op-heading">
        <h1 class="op-title">{{ 'operator.kiosks.title' | translate }}</h1>

        <!-- An installation: the machine on site is bound to its kiosk (plan section 8B). -->
        @if (canActivate()) {
          <a mat-stroked-button routerLink="/operator/activate" data-action="activate">
            <mat-icon>devices</mat-icon>
            {{ 'operator.kiosks.activate' | translate }}
          </a>
        }
      </div>

      @if (screen.problem(); as problem) {
        <app-error-banner [problem]="problem" effect="operator.loadFailedEffect" (retry)="screen.reload()" />
      } @else if (screen.loading()) {
        @for (row of [1, 2, 3]; track row) {
          <div class="neo-skeleton neo-skeleton--block"></div>
        }
      } @else if (cards().length === 0) {
        <app-empty-state
          icon="storefront"
          [message]="'operator.kiosks.empty' | translate"
          [hint]="'operator.kiosks.emptyHint' | translate"
        />
      } @else {
        <app-operator-freshness [at]="screen.at()" [offline]="screen.offline()" />

        @for (card of cards(); track card.id) {
          <article class="neo-card op-card" [attr.data-kiosk]="card.code">
            <a class="op-card--link" [routerLink]="['/operator/kiosks', card.id]">
              <app-operator-kiosk-card [card]="card" />
            </a>

            @if (canRefill() && refillable(card)) {
              <button
                mat-flat-button
                type="button"
                class="op-primary"
                [disabled]="!screen.canAct()"
                (click)="refill(card)"
              >
                <mat-icon>description</mat-icon>
                {{ 'operator.kiosks.refill' | translate }}
              </button>
            }
          </article>
        }
      }
    </div>
  `,
  styleUrl: './operator.scss',
})
export class OperatorKiosksComponent {
  private readonly api = inject(OperatorApiService);
  private readonly auth = inject(AuthService);
  private readonly actions = inject(OperatorActionsService);
  private readonly statuses = inject(KioskLiveStore);

  readonly canRefill = computed(() => this.auth.can(Permissions.paperRefill));
  readonly canActivate = computed(() => this.auth.can(Permissions.kiosksActivate));

  readonly screen = operatorScreen<OperatorKioskCard[]>({
    key: () => 'kiosks',
    fetch: () => this.api.kiosks(),
    // What a card shows changes with each of these; the telemetry that comes
    // every minute from every kiosk is the detail page's to follow.
    events: [
      HubEvents.kioskStatusChanged,
      HubEvents.kioskPrintersUpdated,
      HubEvents.queueUpdated,
      HubEvents.supplyAlertRaised,
      HubEvents.supplyAlertCleared,
      HubEvents.serviceTaskAssigned,
      HubEvents.serviceTaskUpdated,
      HubEvents.incidentRaised,
      HubEvents.incidentResolved,
    ],
    // Each card's status goes to the store every page reads it from (T-254),
    // so a status heard a moment later is shown here without asking again.
    fresh: (cards) => cards.forEach((card) => this.statuses.learn(card.id, card.status as KioskStatus)),
  });

  /** The cards as the server sorted them: the ones that need somebody first. */
  readonly cards = computed(() => this.screen.data() ?? []);

  constructor() {
    void this.screen.reload();
  }

  /** Only a tray with a capacity can be filled. */
  refillable(card: OperatorKioskCard): boolean {
    return Object.values(card.paper).some((tray) => tray.capacity > 0);
  }

  async refill(card: OperatorKioskCard): Promise<void> {
    if (await this.actions.refill(card)) {
      await this.screen.reload();
    }
  }
}
