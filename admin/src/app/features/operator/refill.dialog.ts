import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { TranslatePipe } from '@ngx-translate/core';

import { PaperRefillMode } from '../../core/api/step3.models';
import { InventoryItem, OperatorPaper, RefillRequest, paperItemType } from './operator.models';
import { InventoryUnitPipe } from '../operations/inventory-units';

export interface RefillDialogData {
  /** The kiosk's code, for the title. */
  readonly code: string;

  /** The trays that have a capacity. */
  readonly paper: Readonly<Record<string, OperatorPaper>>;

  /** What the warehouse holds; empty when the operator may not look at it. */
  readonly items: readonly InventoryItem[];

  /** The tray the task or the alert is about, when there is one. */
  readonly preferred?: string;
}

/**
 * "Kağız doldur" (plan section 8B): the size, how the count is said - sheets
 * added, the tray filled, or the count as it stands - and the warehouse item
 * the paper came from, a ream of that size by default.
 *
 * It only gathers the answer; whoever opened it sends it - a refill of its own
 * or the completion of a task.
 */
@Component({
  selector: 'app-operator-refill-dialog',
  imports: [InventoryUnitPipe, 
    FormsModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    TranslatePipe,
  ],
  template: `
    <!-- The code is one word: a title that wraps does not break it at its hyphen. On one line, or the title starts with a space. -->
    <h2 mat-dialog-title>{{ 'operator.refill.title' | translate }} — <span class="neo-nowrap">{{ data.code }}</span></h2>

    <mat-dialog-content class="op-form">
      @if (sizes.length > 1) {
        <mat-button-toggle-group
          [attr.aria-label]="'operator.refill.paper' | translate"
          [ngModel]="paper()"
          (ngModelChange)="choosePaper($event)"
        >
          @for (size of sizes; track size) {
            <mat-button-toggle [value]="size">{{ size }}</mat-button-toggle>
          }
        </mat-button-toggle-group>
      }

      <mat-button-toggle-group
        [attr.aria-label]="'operator.refill.mode' | translate"
        [ngModel]="mode()"
        (ngModelChange)="mode.set($event)"
      >
        @for (option of modes; track option) {
          <mat-button-toggle [value]="option">{{ 'supplies.mode.' + option | translate }}</mat-button-toggle>
        }
      </mat-button-toggle-group>
      <p class="neo-caption">{{ 'supplies.modeHint.' + mode() | translate }}</p>

      @if (mode() !== 'Full') {
        <mat-form-field>
          <mat-label>{{ 'operator.refill.sheets' | translate }}</mat-label>
          <input
            matInput
            type="number"
            inputmode="numeric"
            min="0"
            [max]="maxSheets()"
            [ngModel]="sheets()"
            (ngModelChange)="sheets.set($event)"
          />
        </mat-form-field>
      }

      @if (!sheetsValid() && sheets() !== null) {
        <p class="neo-field-error" role="alert">
          <mat-icon aria-hidden="true">error</mat-icon>
          {{ 'operator.refill.invalid' | translate: { capacity: tray().capacity, current: tray().current } }}
        </p>
      }

      @if (choices().length > 0) {
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>{{ 'operator.refill.item' | translate }}</mat-label>
          <mat-select [ngModel]="itemId()" (ngModelChange)="itemId.set($event)">
            <mat-option [value]="null">{{ 'operator.refill.itemNone' | translate }}</mat-option>
            @for (item of choices(); track item.id) {
              <mat-option [value]="item.id">{{ item.name }}</mat-option>
            }
          </mat-select>
          <!-- Under the field, where a long name cannot cut it off: how many the shelf holds. -->
          @if (item(); as chosen) {
            <mat-hint>
              {{ 'operator.refill.itemStock' | translate: { stock: chosen.stock, unit: (chosen.unit | inventoryUnit) } }}
            </mat-hint>
          }
        </mat-form-field>

        @if (item(); as chosen) {
          <mat-form-field>
            <mat-label>{{ 'operator.refill.quantity' | translate: { unit: (chosen.unit | inventoryUnit) } }}</mat-label>
            <input
              matInput
              type="number"
              inputmode="decimal"
              min="1"
              [max]="chosen.stock"
              [ngModel]="quantity()"
              (ngModelChange)="quantity.set($event)"
            />
          </mat-form-field>

          @if (!quantityValid()) {
            <p class="neo-field-error" role="alert">
              <mat-icon aria-hidden="true">error</mat-icon>
              {{ 'operator.inventory.tooMany' | translate: { stock: chosen.stock, unit: (chosen.unit | inventoryUnit) } }}
            </p>
          }
        }
      }
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close>{{ 'common.cancel' | translate }}</button>
      <button mat-flat-button type="button" [disabled]="!valid()" (click)="save()">
        {{ 'operator.refill.save' | translate }}
      </button>
    </mat-dialog-actions>
  `,
  styleUrl: './operator.scss',
})
export class RefillDialogComponent {
  readonly data = inject<RefillDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<RefillDialogComponent, RefillRequest>);

  readonly modes: readonly PaperRefillMode[] = ['Add', 'Full', 'Exact'];

  /** Only a tray with a capacity can be filled. A4 first: it is the one nearly every order uses. */
  readonly sizes = Object.keys(this.data.paper)
    .filter((size) => this.data.paper[size].capacity > 0)
    .sort((a, b) => b.localeCompare(a));

  /**
   * The tray the dialog opens on: the one the task or the alert is about -
   * otherwise the one that needs paper most. A tray nobody has counted comes
   * before any level; between two equally full ones, A4. (The first version
   * opened on whichever name sorted first, and a "Tam doldur" pressed without
   * looking filled a full A3 tray and left the A4 one as it was.)
   */
  readonly paper = signal(
    this.data.preferred && this.sizes.includes(this.data.preferred) ? this.data.preferred : this.neediest(),
  );

  /** A tray nobody has counted is counted first: "what is in it now" (T-258). */
  readonly mode = signal<PaperRefillMode>(this.data.paper[this.paper()]?.counted === false ? 'Exact' : 'Full');
  readonly sheets = signal<number | null>(null);

  readonly tray = computed<OperatorPaper>(() => this.data.paper[this.paper()] ?? { capacity: 0, current: 0 });

  /** Adding may only fill the tray up to its capacity; a count may be anything up to it. */
  readonly maxSheets = computed(() =>
    this.mode() === 'Add' ? this.tray().capacity - this.tray().current : this.tray().capacity,
  );

  readonly sheetsValid = computed(() => {
    if (this.mode() === 'Full') {
      return true;
    }

    const sheets = this.sheets();
    const minimum = this.mode() === 'Add' ? 1 : 0;

    return sheets !== null && Number.isInteger(sheets) && sheets >= minimum && sheets <= this.maxSheets();
  });

  /** What the warehouse holds of paper in this size. */
  readonly choices = computed(() =>
    this.data.items.filter((item) => item.isActive && item.type === paperItemType[this.paper()]),
  );

  /** A ream of this size by default, when there is one on the shelf. */
  readonly itemId = signal<string | null>(this.defaultItem());
  readonly quantity = signal<number | null>(1);

  readonly item = computed(() => this.choices().find((candidate) => candidate.id === this.itemId()) ?? null);

  readonly quantityValid = computed(() => {
    const item = this.item();
    const quantity = this.quantity();

    return !item || (quantity !== null && quantity > 0 && quantity <= item.stock);
  });

  readonly valid = computed(() => this.sheetsValid() && this.quantityValid());

  choosePaper(size: string): void {
    this.paper.set(size);
    this.itemId.set(this.defaultItem());
    this.sheets.set(null);
  }

  save(): void {
    if (!this.valid()) {
      return;
    }

    const item = this.item();

    this.ref.close({
      paper: this.paper(),
      mode: this.mode(),
      sheets: this.mode() === 'Full' ? 0 : (this.sheets() ?? 0),
      ...(item ? { inventoryItemId: item.id, quantity: this.quantity() ?? 1 } : {}),
    });
  }

  private neediest(): string {
    const need = (size: string): number => {
      const tray = this.data.paper[size];

      return tray.counted === false ? -1 : tray.current / tray.capacity;
    };

    // The order of `sizes` breaks a tie: sort is stable.
    return [...this.sizes].sort((a, b) => need(a) - need(b))[0] ?? 'A4';
  }

  private defaultItem(): string | null {
    const type = paperItemType[this.paper?.() ?? this.sizes[0] ?? 'A4'];

    return this.data.items.find((item) => item.isActive && item.type === type && item.stock > 0)?.id ?? null;
  }
}
