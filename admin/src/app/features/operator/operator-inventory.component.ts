import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { OperatorActionsService, operatorDialog } from './operator-actions.service';
import { OperatorApiService } from './operator-api.service';
import { OperatorFreshnessComponent } from './operator-freshness.component';
import { InventoryItem } from './operator.models';
import { operatorScreen } from './operator-screen';
import { TakeDialogComponent, TakeDialogResult } from './take.dialog';
import { InventoryUnitPipe } from '../operations/inventory-units';

/**
 * "Anbar" (plan section 8B): what the warehouse holds, what is running low,
 * and "Özümə götürürəm" - an operator writing down what they took with them.
 */
@Component({
  selector: 'app-operator-inventory',
  imports: [InventoryUnitPipe, 
    MatButtonModule,
    MatIconModule,
    TranslatePipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    OperatorFreshnessComponent,
  ],
  template: `
    <div class="op-page">
      <h1 class="op-title">{{ 'operator.inventory.title' | translate }}</h1>

      @if (screen.problem(); as problem) {
        <app-error-banner [problem]="problem" effect="operator.loadFailedEffect" (retry)="screen.reload()" />
      } @else if (screen.loading()) {
        @for (row of [1, 2]; track row) {
          <div class="neo-skeleton neo-skeleton--block"></div>
        }
      } @else if (items().length === 0) {
        <app-empty-state
          icon="inventory_2"
          [message]="'operator.inventory.empty' | translate"
          [hint]="'operator.inventory.emptyHint' | translate"
        />
      } @else {
        <app-operator-freshness [at]="screen.at()" [offline]="screen.offline()" />

        @for (item of items(); track item.id) {
          <article class="neo-card op-card" [attr.data-item]="item.sku">
            <div class="op-card__head">
              <span class="op-card__name">
                {{ item.name }}
                <span class="op-card__sub">{{ 'operator.itemType.' + item.type | translate }} · {{ item.sku }}</span>
              </span>
              <strong class="neo-nowrap">
                {{ 'operator.inventory.stock' | translate: { stock: item.stock, unit: (item.unit | inventoryUnit) } }}
              </strong>
            </div>

            <ul class="op-rows">
              @if (item.isLow) {
                <li class="op-row op-row--warning">
                  <mat-icon aria-hidden="true">warning</mat-icon>
                  <span>{{ 'operator.inventory.low' | translate: { min: item.minStock } }}</span>
                </li>
              }
              @if (item.location) {
                <li class="op-row op-row--muted">
                  <mat-icon aria-hidden="true">inventory_2</mat-icon>
                  <span>{{ 'operator.inventory.location' | translate: { location: item.location } }}</span>
                </li>
              }
            </ul>

            @if (canTake()) {
              <button
                mat-stroked-button
                type="button"
                class="op-primary"
                [disabled]="item.stock <= 0 || busy() !== null || !screen.canAct()"
                (click)="take(item)"
              >
                {{ 'operator.inventory.take' | translate }}
              </button>
            }
          </article>
        }
      }
    </div>
  `,
  styleUrl: './operator.scss',
})
export class OperatorInventoryComponent {
  private readonly api = inject(OperatorApiService);
  private readonly auth = inject(AuthService);
  private readonly actions = inject(OperatorActionsService);
  private readonly dialog = inject(MatDialog);

  /** Taking stock for oneself is the operator panel's own right. */
  readonly canTake = computed(() => this.auth.can(Permissions.operatorPanel));

  /** The item something is being sent for. */
  readonly busy = signal<string | null>(null);

  readonly screen = operatorScreen<InventoryItem[]>({
    key: () => 'inventory',
    fetch: () => this.api.inventory(),
    // The warehouse announces nothing over the hub: it is asked again when the
    // connection comes back, and after each thing this page does.
    events: [],
  });

  readonly items = computed(() => this.screen.data() ?? []);

  constructor() {
    void this.screen.reload();
  }

  async take(item: InventoryItem): Promise<void> {
    const answer = await firstValueFrom(
      this.dialog
        .open<TakeDialogComponent, InventoryItem, TakeDialogResult>(TakeDialogComponent, { ...operatorDialog, data: item })
        .afterClosed(),
    );

    if (!answer) {
      return;
    }

    this.busy.set(item.id);

    try {
      await this.actions.send(() => firstValueFrom(this.api.take(item.id, answer.quantity, answer.note)));
      await this.screen.reload();
    } finally {
      this.busy.set(null);
    }
  }
}
