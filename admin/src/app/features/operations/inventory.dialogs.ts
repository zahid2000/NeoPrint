import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { KioskListItem } from '../../core/api/admin-api.service';
import { OperationsApiService } from '../../core/api/operations-api.service';
import { InventoryItemInput } from '../../core/api/step7.models';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { Problem, toProblem } from '../../shared/problem';
import { InventoryItem } from '../operator/operator.models';

export const itemTypes = ['PaperA4Ream', 'PaperA3Ream', 'Toner', 'Drum', 'Other'] as const;

/** Every way stock moves; the ones that go out of the warehouse need a count to leave. */
export const movementTypes = ['Purchase', 'IssueToOperator', 'UsedAtKiosk', 'Adjustment', 'WriteOff'] as const;

/**
 * A warehouse item (plan section 2.15): what it is, how it is counted, the
 * minimum that opens a "running low" incident, where it is kept. An item is
 * never deleted - it is switched off.
 */
@Component({
  selector: 'app-inventory-item-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatSlideToggleModule,
    TranslatePipe,
    ErrorBannerComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ (data.item ? 'ops.inventory.editTitle' : 'ops.inventory.newTitle') | translate }}</h2>
    <mat-dialog-content class="neo-dialog">
      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="ops.inventory.saveFailedEffect" />
      }
      <div class="ops__form">
        <mat-form-field>
          <mat-label>{{ 'ops.inventory.name' | translate }}</mat-label>
          <input matInput maxlength="120" [ngModel]="name()" (ngModelChange)="name.set($event)" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'ops.inventory.sku' | translate }}</mat-label>
          <input matInput maxlength="40" [ngModel]="sku()" (ngModelChange)="sku.set($event)" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'ops.inventory.type' | translate }}</mat-label>
          <mat-select [ngModel]="type()" (ngModelChange)="type.set($event)">
            @for (option of types; track option) {
              <mat-option [value]="option">{{ 'operator.itemType.' + option | translate }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'ops.inventory.unit' | translate }}</mat-label>
          <input matInput maxlength="30" [ngModel]="unit()" (ngModelChange)="unit.set($event)" />
          <mat-hint>{{ 'ops.inventory.unitHint' | translate }}</mat-hint>
        </mat-form-field>
        @if (paper()) {
          <mat-form-field>
            <mat-label>{{ 'ops.inventory.sheetsPerUnit' | translate }}</mat-label>
            <input matInput type="number" min="1" [ngModel]="sheetsPerUnit()" (ngModelChange)="sheetsPerUnit.set($event)" />
          </mat-form-field>
        }
        <mat-form-field>
          <mat-label>{{ 'ops.inventory.minStock' | translate }}</mat-label>
          <input matInput type="number" min="0" [ngModel]="minStock()" (ngModelChange)="minStock.set($event)" />
          <mat-hint>{{ 'ops.inventory.minStockHint' | translate }}</mat-hint>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'ops.inventory.location' | translate }}</mat-label>
          <input matInput maxlength="120" [ngModel]="location()" (ngModelChange)="location.set($event)" />
        </mat-form-field>
        @if (!paper()) {
          <mat-form-field class="ops__wide">
            <mat-label>{{ 'ops.inventory.models' | translate }}</mat-label>
            <input matInput maxlength="400" [ngModel]="models()" (ngModelChange)="models.set($event)" />
            <mat-hint>{{ 'ops.inventory.modelsHint' | translate }}</mat-hint>
          </mat-form-field>
        }
        <mat-slide-toggle class="ops__wide" [checked]="active()" (change)="active.set($event.checked)">
          {{ 'ops.inventory.active' | translate }}
        </mat-slide-toggle>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions class="neo-dialog__actions">
      <button mat-button type="button" [disabled]="busy()" (click)="ref.close(false)">{{ 'common.cancel' | translate }}</button>
      <button mat-flat-button type="button" [disabled]="!valid() || busy()" (click)="save()">{{ 'common.save' | translate }}</button>
    </mat-dialog-actions>
  `,
  styleUrl: './operations.scss',
})
export class InventoryItemDialogComponent {
  readonly data = inject<{ item: InventoryItem | null }>(MAT_DIALOG_DATA);
  readonly ref = inject<MatDialogRef<InventoryItemDialogComponent, boolean>>(MatDialogRef);
  private readonly api = inject(OperationsApiService);

  readonly types = itemTypes;
  private readonly item = this.data.item;

  readonly name = signal(this.item?.name ?? '');
  readonly sku = signal(this.item?.sku ?? '');
  readonly type = signal<string>(this.item?.type ?? 'PaperA4Ream');
  readonly unit = signal(this.item?.unit ?? '');
  readonly sheetsPerUnit = signal<number | null>(this.item?.sheetsPerUnit ?? 500);
  readonly minStock = signal<number>(this.item?.minStock ?? 0);
  readonly location = signal(this.item?.location ?? '');
  readonly models = signal((this.item?.compatiblePrinterModels ?? []).join(', '));
  readonly active = signal(this.item?.isActive ?? true);
  readonly busy = signal(false);
  readonly problem = signal<Problem | null>(null);

  readonly paper = computed(() => this.type().startsWith('Paper'));
  readonly valid = computed(
    () => this.name().trim().length > 0 && this.sku().trim().length > 0 && this.unit().trim().length > 0 && Number(this.minStock()) >= 0,
  );

  async save(): Promise<void> {
    const input: InventoryItemInput = {
      sku: this.sku().trim(),
      name: this.name().trim(),
      type: this.type(),
      compatiblePrinterModels: this.paper()
        ? []
        : this.models()
            .split(',')
            .map((model) => model.trim())
            .filter(Boolean),
      unit: this.unit().trim(),
      sheetsPerUnit: this.paper() ? Number(this.sheetsPerUnit()) || null : null,
      minStock: Number(this.minStock()),
      location: this.location().trim() || null,
      isActive: this.active(),
    };

    this.busy.set(true);
    this.problem.set(null);

    try {
      await firstValueFrom(this.item ? this.api.updateItem(this.item.id, input) : this.api.createItem(input));
      this.ref.close(true);
    } catch (error) {
      this.problem.set(toProblem(error));
      this.busy.set(false);
    }
  }
}

/**
 * A movement of one item (plan section 2.15): bought, handed to an operator,
 * used at a kiosk, a correction after a count, written off. The stock never
 * goes below nought; the server refuses it and says how much there is.
 */
@Component({
  selector: 'app-inventory-movement-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule, TranslatePipe, ErrorBannerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ 'ops.inventory.moveTitle' | translate: { item: data.item.name } }}</h2>
    <mat-dialog-content class="neo-dialog">
      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="ops.inventory.moveFailedEffect" />
      }
      <p class="neo-caption">{{ 'ops.inventory.stockNow' | translate: { stock: data.item.stock, unit: data.item.unit } }}</p>
      <div class="ops__form">
        <mat-form-field>
          <mat-label>{{ 'ops.inventory.moveType' | translate }}</mat-label>
          <mat-select [ngModel]="type()" (ngModelChange)="type.set($event)">
            @for (option of types; track option) {
              <mat-option [value]="option">{{ 'ops.inventory.movement.' + option | translate }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'ops.inventory.quantity' | translate }}</mat-label>
          <input matInput type="number" [ngModel]="quantity()" (ngModelChange)="quantity.set($event)" />
          <mat-hint>{{ 'ops.inventory.quantityHint.' + type() | translate }}</mat-hint>
        </mat-form-field>
        @if (type() === 'UsedAtKiosk') {
          <mat-form-field class="ops__wide">
            <mat-label>{{ 'ops.kiosk' | translate }}</mat-label>
            <mat-select [ngModel]="kioskId()" (ngModelChange)="kioskId.set($event)">
              @for (kiosk of data.kiosks; track kiosk.id) {
                <mat-option [value]="kiosk.id">{{ kiosk.code }} — {{ kiosk.name }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        }
        <mat-form-field class="ops__wide">
          <mat-label>{{ 'ops.inventory.note' | translate }}</mat-label>
          <input matInput maxlength="300" [ngModel]="note()" (ngModelChange)="note.set($event)" />
        </mat-form-field>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions class="neo-dialog__actions">
      <button mat-button type="button" [disabled]="busy()" (click)="ref.close(false)">{{ 'common.cancel' | translate }}</button>
      <button mat-flat-button type="button" [disabled]="!valid() || busy()" (click)="save()">{{ 'ops.inventory.record' | translate }}</button>
    </mat-dialog-actions>
  `,
  styleUrl: './operations.scss',
})
export class InventoryMovementDialogComponent {
  readonly data = inject<{ item: InventoryItem; kiosks: readonly KioskListItem[] }>(MAT_DIALOG_DATA);
  readonly ref = inject<MatDialogRef<InventoryMovementDialogComponent, boolean>>(MatDialogRef);
  private readonly api = inject(OperationsApiService);

  readonly types = movementTypes;

  readonly type = signal<string>('Purchase');
  readonly quantity = signal<number | null>(null);
  readonly kioskId = signal<string | null>(null);
  readonly note = signal('');
  readonly busy = signal(false);
  readonly problem = signal<Problem | null>(null);

  /** Positive, except a correction, which may go either way but not be nought. */
  readonly valid = computed(() => {
    const quantity = Number(this.quantity());

    if (this.quantity() === null || !Number.isFinite(quantity) || quantity === 0) {
      return false;
    }

    return (this.type() === 'Adjustment' || quantity > 0) && (this.type() !== 'UsedAtKiosk' || !!this.kioskId());
  });

  async save(): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);

    try {
      await firstValueFrom(
        this.api.move({
          itemId: this.data.item.id,
          type: this.type(),
          quantity: Number(this.quantity()),
          kioskId: this.type() === 'UsedAtKiosk' ? this.kioskId() : null,
          note: this.note().trim() || null,
        }),
      );
      this.ref.close(true);
    } catch (error) {
      this.problem.set(toProblem(error));
      this.busy.set(false);
    }
  }
}
