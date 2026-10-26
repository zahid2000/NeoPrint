import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { OperationsApiService } from '../../core/api/operations-api.service';
import { InventoryMovement } from '../../core/api/step7.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { HubEvents } from '../../core/realtime/hub-events';
import { KioskLiveStore } from '../../core/realtime/kiosk-live.store';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { FilterBarComponent } from '../../shared/filter-bar.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { PagerComponent } from '../money/money-shared';
import { InventoryItem } from '../operator/operator.models';
import { InventoryItemDialogComponent, InventoryMovementDialogComponent } from './inventory.dialogs';
import { InventoryAmountPipe, isUnitWord } from './inventory-units';

const pageSize = 30;

/**
 * "Anbar" (plan sections 2.15, 8A.3): what is kept for the kiosks - the
 * running-low ones first - and every movement of it. Stock moves with an
 * operator's refill and task (live, through the service task events) and with
 * what is recorded here.
 */
@Component({
  selector: 'app-inventory',
  imports: [
    FormsModule,
    RouterLink,
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatIconModule,
    MatSelectModule,
    MatTableModule,
    MatTabsModule,
    TranslatePipe,
    DateTimePipe,
    InventoryAmountPipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    FilterBarComponent,
    PageHeaderComponent,
    PagerComponent,
    StatusChipComponent,
  ],
  template: `
    <div class="neo-page">
      <app-page-header [title]="'ops.inventory.title' | translate" [description]="'ops.inventory.description' | translate">
        @if (canManage()) {
          <button mat-flat-button type="button" (click)="editItem(null)">
            <mat-icon>add</mat-icon>
            {{ 'ops.inventory.new' | translate }}
          </button>
        }
      </app-page-header>

      <mat-tab-group animationDuration="150ms" (selectedIndexChange)="tab.set($event)">
        <mat-tab [label]="'ops.inventory.tabItems' | translate">
          <section class="ops__tab">
            <app-filter-bar>
              <mat-checkbox [ngModel]="includeInactive()" (ngModelChange)="setInactive($event)">
                {{ 'ops.inventory.showInactive' | translate }}
              </mat-checkbox>
            </app-filter-bar>

            @if (problem(); as problem) {
              <app-error-banner [problem]="problem" effect="ops.inventory.loadFailedEffect" (retry)="loadItems()" />
            } @else if (items() === null) {
              <div class="neo-skeleton neo-skeleton--block"></div>
            } @else if (items()!.length === 0) {
              <app-empty-state icon="warehouse" [message]="'ops.inventory.empty' | translate" [hint]="'ops.inventory.emptyHint' | translate" />
            } @else {
              <div class="neo-table-wrapper">
                <table mat-table class="neo-table" [dataSource]="items()!">
                  <ng-container matColumnDef="name">
                    <th mat-header-cell *matHeaderCellDef>{{ 'ops.inventory.name' | translate }}</th>
                    <td mat-cell *matCellDef="let item">
                      {{ item.name }}
                      <span class="neo-caption ops__block">{{ item.sku }}@if (!item.isActive) { · {{ 'ops.inventory.inactive' | translate }} }</span>
                    </td>
                  </ng-container>
                  <ng-container matColumnDef="type">
                    <th mat-header-cell *matHeaderCellDef>{{ 'ops.inventory.type' | translate }}</th>
                    <td mat-cell *matCellDef="let item">{{ 'operator.itemType.' + item.type | translate }}</td>
                  </ng-container>
                  <ng-container matColumnDef="stock">
                    <th mat-header-cell *matHeaderCellDef class="ops__number">{{ 'ops.inventory.stock' | translate }}</th>
                    <td mat-cell *matCellDef="let item" class="ops__number" data-cell="stock">
                      <span class="neo-nowrap">{{ item.stock | inventoryAmount: item.unit }}</span>
                      @if (!unitWord(item.unit)) {
                        <span class="neo-caption ops__block ops__invalid" data-unit-broken>{{ 'ops.inventory.unitBroken' | translate }}</span>
                      }
                      @if (item.isLow) {
                        <span class="ops__block">
                          <app-status-chip status="Low" toneOverride="warning" [text]="'ops.inventory.low' | translate" />
                        </span>
                        <span class="neo-caption ops__block" data-low-why>
                          {{ 'ops.inventory.lowWhy' | translate: { stock: item.stock | inventoryAmount: null, min: item.minStock | inventoryAmount: null } }}
                        </span>
                      }
                    </td>
                  </ng-container>
                  <ng-container matColumnDef="min">
                    <th mat-header-cell *matHeaderCellDef class="ops__number">{{ 'ops.inventory.minStock' | translate }}</th>
                    <td mat-cell *matCellDef="let item" class="ops__number neo-nowrap">{{ item.minStock | inventoryAmount: item.unit }}</td>
                  </ng-container>
                  <ng-container matColumnDef="models">
                    <th mat-header-cell *matHeaderCellDef>{{ 'ops.inventory.models' | translate }}</th>
                    <td mat-cell *matCellDef="let item">{{ item.compatiblePrinterModels.join(', ') }}</td>
                  </ng-container>
                  <ng-container matColumnDef="location">
                    <th mat-header-cell *matHeaderCellDef>{{ 'ops.inventory.location' | translate }}</th>
                    <td mat-cell *matCellDef="let item">{{ item.location }}</td>
                  </ng-container>
                  <ng-container matColumnDef="actions">
                    <th mat-header-cell *matHeaderCellDef></th>
                    <td mat-cell *matCellDef="let item">
                      @if (canManage()) {
                        <div class="ops__actions">
                          <button mat-stroked-button type="button" (click)="move(item)">{{ 'ops.inventory.move' | translate }}</button>
                          <button mat-icon-button type="button" [attr.aria-label]="'common.edit' | translate" (click)="editItem(item)">
                            <mat-icon>edit</mat-icon>
                          </button>
                        </div>
                      }
                    </td>
                  </ng-container>

                  <tr mat-header-row *matHeaderRowDef="itemColumns"></tr>
                  <tr mat-row *matRowDef="let row; columns: itemColumns"></tr>
                </table>
              </div>
            }
          </section>
        </mat-tab>

        <mat-tab [label]="'ops.inventory.tabMovements' | translate">
          <section class="ops__tab">
            <app-filter-bar>
              <mat-form-field>
                <mat-label>{{ 'ops.inventory.item' | translate }}</mat-label>
                <mat-select [ngModel]="itemId()" (ngModelChange)="setItem($event)">
                  <mat-option [value]="null">{{ 'ops.inventory.allItems' | translate }}</mat-option>
                  @for (item of items() ?? []; track item.id) {
                    <mat-option [value]="item.id">{{ item.name }}</mat-option>
                  }
                </mat-select>
              </mat-form-field>
              <mat-form-field>
                <mat-label>{{ 'ops.kiosk' | translate }}</mat-label>
                <mat-select [ngModel]="kioskId()" (ngModelChange)="setKiosk($event)">
                  <mat-option [value]="null">{{ 'ops.allKiosks' | translate }}</mat-option>
                  @for (kiosk of kiosks(); track kiosk.id) {
                    <mat-option [value]="kiosk.id">{{ kiosk.code }} — {{ kiosk.name }}</mat-option>
                  }
                </mat-select>
              </mat-form-field>
            </app-filter-bar>

            @if (movements() === null) {
              <div class="neo-skeleton neo-skeleton--block"></div>
            } @else if (movements()!.length === 0) {
              <app-empty-state icon="history" [message]="'ops.inventory.noMovements' | translate" />
            } @else {
              <div class="neo-table-wrapper">
                <table mat-table class="neo-table" [dataSource]="movements()!">
                  <ng-container matColumnDef="at">
                    <th mat-header-cell *matHeaderCellDef>{{ 'ops.inventory.at' | translate }}</th>
                    <td mat-cell *matCellDef="let movement" class="neo-nowrap">{{ movement.at | dateTime }}</td>
                  </ng-container>
                  <ng-container matColumnDef="item">
                    <th mat-header-cell *matHeaderCellDef>{{ 'ops.inventory.item' | translate }}</th>
                    <td mat-cell *matCellDef="let movement">{{ movement.itemName }}</td>
                  </ng-container>
                  <ng-container matColumnDef="type">
                    <th mat-header-cell *matHeaderCellDef>{{ 'ops.inventory.moveType' | translate }}</th>
                    <td mat-cell *matCellDef="let movement">{{ 'ops.inventory.movement.' + movement.type | translate }}</td>
                  </ng-container>
                  <ng-container matColumnDef="quantity">
                    <th mat-header-cell *matHeaderCellDef class="ops__number">{{ 'ops.inventory.quantity' | translate }}</th>
                    <td mat-cell *matCellDef="let movement" class="ops__number" [class.ops__added]="movement.quantity > 0">
                      {{ (movement.quantity > 0 ? '+' : '') + movement.quantity }}
                    </td>
                  </ng-container>
                  <ng-container matColumnDef="after">
                    <th mat-header-cell *matHeaderCellDef class="ops__number">{{ 'ops.inventory.after' | translate }}</th>
                    <td mat-cell *matCellDef="let movement" class="ops__number">{{ movement.stockAfter }}</td>
                  </ng-container>
                  <ng-container matColumnDef="kiosk">
                    <th mat-header-cell *matHeaderCellDef>{{ 'ops.kiosk' | translate }}</th>
                    <td mat-cell *matCellDef="let movement" class="neo-nowrap">
                      @if (movement.kioskId) {
                        <a [routerLink]="['/kiosks', movement.kioskId]">{{ codeOf(movement.kioskId) }}</a>
                      }
                    </td>
                  </ng-container>
                  <ng-container matColumnDef="note">
                    <th mat-header-cell *matHeaderCellDef>{{ 'ops.inventory.note' | translate }}</th>
                    <td mat-cell *matCellDef="let movement">{{ movement.note }}</td>
                  </ng-container>

                  <tr mat-header-row *matHeaderRowDef="movementColumns"></tr>
                  <tr mat-row *matRowDef="let row; columns: movementColumns"></tr>
                </table>
              </div>
              <app-money-pager [total]="total()" [page]="page()" [pageSize]="pageSize" countKey="ops.inventory.count" (go)="go($event)" />
            }
          </section>
        </mat-tab>
      </mat-tab-group>
    </div>
  `,
  styles: `
    .ops__tab {
      padding-block-start: var(--space-4);
    }
  `,
  styleUrl: './operations.scss',
})
export class InventoryComponent {
  private readonly api = inject(OperationsApiService);
  private readonly auth = inject(AuthService);
  private readonly kioskStore = inject(KioskLiveStore);
  private readonly dialog = inject(MatDialog);
  private readonly toast = inject(NotifyService);

  readonly pageSize = pageSize;
  readonly itemColumns = ['name', 'type', 'stock', 'min', 'models', 'location', 'actions'];
  readonly movementColumns = ['at', 'item', 'type', 'quantity', 'after', 'kiosk', 'note'];

  readonly tab = signal(0);
  readonly includeInactive = signal(false);
  readonly items = signal<readonly InventoryItem[] | null>(null);
  readonly problem = signal<Problem | null>(null);
  readonly itemId = signal<string | null>(null);
  readonly kioskId = signal<string | null>(null);
  readonly page = signal(1);
  readonly movements = signal<readonly InventoryMovement[] | null>(null);
  readonly total = signal(0);

  readonly canManage = computed(() => this.auth.can(Permissions.inventoryManage));
  readonly kiosks = computed(() => this.kioskStore.kiosks() ?? []);

  constructor() {
    this.kioskStore.watch();

    // An operator's finished task or refill takes stock out of the warehouse.
    liveRefresh({
      events: [HubEvents.serviceTaskUpdated],
      reload: () => Promise.all([this.loadItems(true), this.loadMovements(true)]),
    });

    void this.loadItems();
    void this.loadMovements();
  }

  /** An item kept before T-323 may have a number for its unit: it is not written, and the row asks for a fix. */
  unitWord(unit: string): boolean {
    return isUnitWord(unit);
  }

  codeOf(kioskId: string): string {
    return this.kiosks().find((kiosk) => kiosk.id === kioskId)?.code ?? '';
  }

  setInactive(include: boolean): void {
    this.includeInactive.set(include);
    void this.loadItems();
  }

  setItem(itemId: string | null): void {
    this.itemId.set(itemId);
    this.page.set(1);
    void this.loadMovements();
  }

  setKiosk(kioskId: string | null): void {
    this.kioskId.set(kioskId);
    this.page.set(1);
    void this.loadMovements();
  }

  go(page: number): void {
    this.page.set(page);
    void this.loadMovements();
  }

  async loadItems(quiet = false): Promise<void> {
    if (!quiet) {
      this.problem.set(null);
    }

    try {
      this.items.set(await firstValueFrom(this.api.items(this.includeInactive())));
    } catch (error) {
      if (!quiet) {
        this.problem.set(toProblem(error));
      }
    }
  }

  async loadMovements(quiet = false): Promise<void> {
    try {
      const result = await firstValueFrom(this.api.movements(this.itemId(), this.kioskId(), this.page(), pageSize));
      this.movements.set(result.items);
      this.total.set(result.total);
    } catch (error) {
      if (!quiet) {
        this.movements.set([]);
        this.toast.error(toProblem(error).message);
      }
    }
  }

  editItem(item: InventoryItem | null): void {
    this.dialog
      .open(InventoryItemDialogComponent, { data: { item }, width: '640px', maxWidth: 'calc(100vw - 32px)' })
      .afterClosed()
      .subscribe((saved) => {
        if (saved) {
          this.toast.success('ops.inventory.saved');
          void this.loadItems(true);
          // A new item's initial stock is its first purchase (T-323).
          void this.loadMovements(true);
        }
      });
  }

  move(item: InventoryItem): void {
    this.dialog
      .open(InventoryMovementDialogComponent, { data: { item, kiosks: this.kiosks() }, width: '560px', maxWidth: 'calc(100vw - 32px)' })
      .afterClosed()
      .subscribe((saved) => {
        if (saved) {
          this.toast.success('ops.inventory.moved');
          void this.loadItems(true);
          void this.loadMovements(true);
        }
      });
  }
}
