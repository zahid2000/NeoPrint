import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { TranslatePipe } from '@ngx-translate/core';

import { InventoryItem, SupplyLevel } from './operator.models';

/** What can be recorded as replaced; paper is filled, not replaced. */
export const replaceableResources = ['Toner', 'Drum', 'WasteToner', 'Other'] as const;

/** The warehouse item types that go with each. */
const itemTypesOf: Readonly<Record<string, readonly string[]>> = {
  Toner: ['Toner'],
  Drum: ['Drum'],
  WasteToner: ['Other'],
  Other: ['Other'],
};

export interface ReplacedDialogData {
  /** Already in the reader's language. */
  readonly title: string;

  /** The kiosk's code, shown after the title. */
  readonly code?: string;

  /**
   * Whether to ask what was replaced. A task already says it ("Toner
   * dəyişmək"): then only the warehouse item and the note are asked for.
   */
  readonly askWhat: boolean;

  /** What a task is about, when {@link askWhat} is false. */
  readonly resource?: string;

  /** What the printer reports, for choosing which cartridge. */
  readonly levels: readonly SupplyLevel[];

  /** What the warehouse holds; empty when the operator may not look at it. */
  readonly items: readonly InventoryItem[];
}

export interface ReplacedDialogResult {
  readonly resource: string;
  readonly variant?: string;
  readonly inventoryItemId?: string;
  readonly quantity?: number;
  readonly note?: string;
}

/**
 * "Toner dəyişdirildi" and the completion of a task that is not a refill
 * (plan section 8B): what was changed, which one, what was taken from the
 * warehouse for it, and a note.
 */
@Component({
  selector: 'app-operator-replaced-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    TranslatePipe,
  ],
  template: `
    <!-- The code is one word: a title that wraps does not break it at its hyphen. On one line, or the title starts with a space. -->
    <h2 mat-dialog-title>{{ data.title }}{{ data.code ? ' — ' : '' }}<span class="neo-nowrap">{{ data.code }}</span></h2>

    <mat-dialog-content class="op-form">
      @if (data.askWhat) {
        <p class="neo-caption">{{ 'operator.replaced.explanation' | translate }}</p>

        <mat-form-field>
          <mat-label>{{ 'operator.replaced.what' | translate }}</mat-label>
          <mat-select [ngModel]="resource()" (ngModelChange)="chooseResource($event)">
            @for (option of resources; track option) {
              <mat-option [value]="option">{{ 'operator.resource.' + option | translate }}</mat-option>
            }
          </mat-select>
        </mat-form-field>

        @if (variants().length > 0) {
          <mat-form-field>
            <mat-label>{{ 'operator.replaced.which' | translate }}</mat-label>
            <mat-select [ngModel]="variant()" (ngModelChange)="variant.set($event)">
              @for (option of variants(); track option) {
                <mat-option [value]="option">{{ option }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        } @else {
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>{{ 'operator.replaced.which' | translate }}</mat-label>
            <input matInput maxlength="100" [ngModel]="variant()" (ngModelChange)="variant.set($event)" />
            <mat-hint>{{ 'operator.replaced.whichHint' | translate }}</mat-hint>
          </mat-form-field>
        }
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
              {{ 'operator.refill.itemStock' | translate: { stock: chosen.stock, unit: chosen.unit } }}
            </mat-hint>
          }
        </mat-form-field>

        @if (item(); as chosen) {
          <mat-form-field>
            <mat-label>{{ 'operator.refill.quantity' | translate: { unit: chosen.unit } }}</mat-label>
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
              {{ 'operator.inventory.tooMany' | translate: { stock: chosen.stock, unit: chosen.unit } }}
            </p>
          }
        }
      }

      <mat-form-field>
        <mat-label>{{ 'operator.replaced.note' | translate }}</mat-label>
        <textarea matInput rows="2" maxlength="500" [ngModel]="note()" (ngModelChange)="note.set($event)"></textarea>
      </mat-form-field>
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close>{{ 'common.cancel' | translate }}</button>
      <button mat-flat-button type="button" [disabled]="!quantityValid()" (click)="save()">
        {{ 'operator.refill.save' | translate }}
      </button>
    </mat-dialog-actions>
  `,
  styleUrl: './operator.scss',
})
export class ReplacedDialogComponent {
  readonly data = inject<ReplacedDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<ReplacedDialogComponent, ReplacedDialogResult>);

  readonly resources = replaceableResources;

  readonly resource = signal<string>(this.data.resource ?? 'Toner');
  readonly variant = signal<string>('');
  readonly itemId = signal<string | null>(null);
  readonly quantity = signal<number | null>(1);
  readonly note = signal('');

  /** The cartridges the printer reports of this kind, by the name it gives them. */
  readonly variants = computed(() => [
    ...new Set(
      this.data.levels
        .filter((level) => level.resource === this.resource() && level.variant)
        .map((level) => level.variant as string),
    ),
  ]);

  readonly choices = computed(() => {
    const types = itemTypesOf[this.resource()] ?? [];

    return this.data.items.filter((item) => item.isActive && types.includes(item.type));
  });

  readonly item = computed(() => this.choices().find((candidate) => candidate.id === this.itemId()) ?? null);

  readonly quantityValid = computed(() => {
    const item = this.item();
    const quantity = this.quantity();

    return !item || (quantity !== null && quantity > 0 && quantity <= item.stock);
  });

  chooseResource(resource: string): void {
    this.resource.set(resource);
    this.variant.set('');
    this.itemId.set(null);
  }

  save(): void {
    if (!this.quantityValid()) {
      return;
    }

    const item = this.item();
    const variant = this.variant().trim();
    const note = this.note().trim();

    this.ref.close({
      resource: this.resource(),
      ...(variant ? { variant } : {}),
      ...(item ? { inventoryItemId: item.id, quantity: this.quantity() ?? 1 } : {}),
      ...(note ? { note } : {}),
    });
  }
}
