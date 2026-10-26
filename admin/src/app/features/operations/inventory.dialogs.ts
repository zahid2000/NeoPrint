import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { KioskListItem } from '../../core/api/admin-api.service';
import { OperationsApiService } from '../../core/api/operations-api.service';
import { InventoryItemInput } from '../../core/api/step7.models';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { Problem, toProblem } from '../../shared/problem';
import { InventoryItem } from '../operator/operator.models';
import { InventoryAmountPipe, InventoryUnitPipe, inventoryUnits, isKnownUnit, isUnitWord } from './inventory-units';

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
    InventoryUnitPipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ (data.item ? 'ops.inventory.editTitle' : 'ops.inventory.newTitle') | translate }}</h2>
    <mat-dialog-content class="neo-dialog">
      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="ops.inventory.saveFailedEffect" />
      }
      <div class="ops__form">
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>{{ 'ops.inventory.name' | translate }}</mat-label>
          <input matInput maxlength="120" [ngModel]="name()" (ngModelChange)="name.set($event)" />
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>{{ 'ops.inventory.sku' | translate }}</mat-label>
          <input matInput maxlength="40" [ngModel]="sku()" (ngModelChange)="sku.set($event)" />
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>{{ 'ops.inventory.type' | translate }}</mat-label>
          <mat-select [ngModel]="type()" (ngModelChange)="chooseType($event)">
            @for (option of types; track option) {
              <mat-option [value]="option">{{ 'operator.itemType.' + option | translate }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>{{ 'ops.inventory.unit' | translate }}</mat-label>
          <mat-select data-field="unit" [ngModel]="unitChoice()" (ngModelChange)="chooseUnit($event)">
            @for (option of units; track option) {
              <mat-option [value]="option">{{ option | inventoryUnit }}</mat-option>
            }
            <mat-option value="other">{{ 'ops.inventory.unitOther' | translate }}</mat-option>
          </mat-select>
          <mat-hint>{{ 'ops.inventory.unitHint' | translate }}</mat-hint>
        </mat-form-field>
        @if (unitChoice() === 'other') {
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>{{ 'ops.inventory.unitCustom' | translate }}</mat-label>
            <input matInput data-field="unitCustom" maxlength="30" [ngModel]="customUnit()" (ngModelChange)="customUnit.set($event)" />
            @if (customUnit().trim() && !unitValid()) {
              <mat-hint class="ops__invalid" data-error="unit">{{ 'ops.inventory.unitInvalid' | translate }}</mat-hint>
            } @else {
              <mat-hint>{{ 'ops.inventory.unitCustomHint' | translate }}</mat-hint>
            }
          </mat-form-field>
        }
        @if (paper()) {
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>{{ 'ops.inventory.sheetsPerUnit' | translate }}</mat-label>
            <input matInput type="number" min="1" [ngModel]="sheetsPerUnit()" (ngModelChange)="sheetsPerUnit.set($event)" />
            <mat-hint>{{ 'ops.inventory.sheetsPerUnitHint' | translate: { unit: unitWord() } }}</mat-hint>
          </mat-form-field>
        }
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>{{ 'ops.inventory.minStock' | translate }}</mat-label>
          <input matInput type="number" min="0" [ngModel]="minStock()" (ngModelChange)="minStock.set($event)" />
          <mat-hint>{{ 'ops.inventory.minStockHint' | translate: { unit: unitWord() } }}</mat-hint>
        </mat-form-field>
        @if (!data.item) {
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>{{ 'ops.inventory.initialStock' | translate }}</mat-label>
            <input matInput data-field="initialStock" type="number" min="0" [ngModel]="initialStock()" (ngModelChange)="initialStock.set($event)" />
            <mat-hint>{{ 'ops.inventory.initialStockHint' | translate: { unit: unitWord() } }}</mat-hint>
          </mat-form-field>
        }
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>{{ 'ops.inventory.location' | translate }}</mat-label>
          <input matInput maxlength="120" [ngModel]="location()" (ngModelChange)="location.set($event)" />
        </mat-form-field>
        @if (!paper()) {
          <mat-form-field class="ops__wide" subscriptSizing="dynamic">
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
  readonly units = inventoryUnits;
  private readonly item = this.data.item;
  private readonly translate = inject(TranslateService);

  readonly name = signal(this.item?.name ?? '');
  readonly sku = signal(this.item?.sku ?? '');
  readonly type = signal<string>(this.item?.type ?? 'PaperA4Ream');
  /** A known unit, or "other" with the warehouse's own word written below (T-323). */
  readonly unitChoice = signal<string>(!this.item ? 'ream' : isKnownUnit(this.item.unit) ? this.item.unit : 'other');
  readonly customUnit = signal(this.item && !isKnownUnit(this.item.unit) ? this.item.unit : '');
  readonly initialStock = signal<number | null>(0);
  readonly unit = computed(() => (this.unitChoice() === 'other' ? this.customUnit().trim() : this.unitChoice()));
  readonly unitValid = computed(() => isUnitWord(this.unit()));
  /** The unit as the hints say it: "bağlama", or the word written. */
  readonly unitWord = computed(() =>
    isKnownUnit(this.unit())
      ? (this.translate.instant('ops.inventory.units.' + this.unit()) as string)
      : this.unitValid()
        ? this.unit()
        : '…',
  );
  private unitTouched = false;
  readonly sheetsPerUnit = signal<number | null>(this.item?.sheetsPerUnit ?? 500);
  readonly minStock = signal<number>(this.item?.minStock ?? 0);
  readonly location = signal(this.item?.location ?? '');
  readonly models = signal((this.item?.compatiblePrinterModels ?? []).join(', '));
  readonly active = signal(this.item?.isActive ?? true);
  readonly busy = signal(false);
  readonly problem = signal<Problem | null>(null);

  readonly paper = computed(() => this.type().startsWith('Paper'));
  readonly valid = computed(
    () =>
      this.name().trim().length > 0 &&
      this.sku().trim().length > 0 &&
      this.unitValid() &&
      Number(this.minStock()) >= 0 &&
      (this.item !== null || Number(this.initialStock() ?? 0) >= 0),
  );

  chooseUnit(choice: string): void {
    this.unitTouched = true;
    this.unitChoice.set(choice);
  }

  /** A new item's unit follows its type until one is chosen: paper in reams, the rest by the piece. */
  chooseType(type: string): void {
    this.type.set(type);

    if (!this.item && !this.unitTouched) {
      this.unitChoice.set(type.startsWith('Paper') ? 'ream' : 'piece');
    }
  }

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
      unit: this.unit(),
      sheetsPerUnit: this.paper() ? Number(this.sheetsPerUnit()) || null : null,
      minStock: Number(this.minStock()),
      location: this.location().trim() || null,
      isActive: this.active(),
      ...(this.item ? {} : { initialStock: Number(this.initialStock() ?? 0) || 0 }),
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
  imports: [
    FormsModule,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    TranslatePipe,
    ErrorBannerComponent,
    InventoryAmountPipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ 'ops.inventory.moveTitle' | translate: { item: data.item.name } }}</h2>
    <mat-dialog-content class="neo-dialog">
      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="ops.inventory.moveFailedEffect" />
      }
      <p class="neo-caption">{{ 'ops.inventory.stockNow' | translate: { amount: data.item.stock | inventoryAmount: data.item.unit } }}</p>
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
