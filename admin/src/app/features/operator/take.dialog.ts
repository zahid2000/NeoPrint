import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { TranslatePipe } from '@ngx-translate/core';

import { InventoryItem } from './operator.models';
import { InventoryUnitPipe } from '../operations/inventory-units';

export interface TakeDialogResult {
  readonly quantity: number;
  readonly note: string | null;
}

/**
 * "Özümə götürürəm" (plan section 8B, "Anbar"): how many of an item the
 * operator took from the shelf. Never more than the shelf holds - the server
 * says the same.
 */
@Component({
  selector: 'app-operator-take-dialog',
  imports: [InventoryUnitPipe, 
    FormsModule,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    TranslatePipe,
  ],
  template: `
    <h2 mat-dialog-title>{{ 'operator.inventory.takeTitle' | translate: { name: item.name } }}</h2>

    <mat-dialog-content class="op-form">
      <p class="neo-caption">{{ 'operator.inventory.takeExplanation' | translate }}</p>

      <mat-form-field>
        <mat-label>{{ 'operator.inventory.quantity' | translate: { unit: (item.unit | inventoryUnit) } }}</mat-label>
        <input
          matInput
          type="number"
          inputmode="decimal"
          min="1"
          [max]="item.stock"
          [ngModel]="quantity()"
          (ngModelChange)="quantity.set($event)"
        />
      </mat-form-field>

      @if (!valid()) {
        <p class="neo-field-error" role="alert">
          <mat-icon aria-hidden="true">error</mat-icon>
          {{ 'operator.inventory.tooMany' | translate: { stock: item.stock, unit: (item.unit | inventoryUnit) } }}
        </p>
      }

      <mat-form-field>
        <mat-label>{{ 'operator.inventory.note' | translate }}</mat-label>
        <textarea matInput rows="2" maxlength="500" [ngModel]="note()" (ngModelChange)="note.set($event)"></textarea>
      </mat-form-field>
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
export class TakeDialogComponent {
  readonly item = inject<InventoryItem>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<TakeDialogComponent, TakeDialogResult>);

  readonly quantity = signal<number | null>(1);
  readonly note = signal('');

  readonly valid = computed(() => {
    const quantity = this.quantity();

    return quantity !== null && quantity > 0 && quantity <= this.item.stock;
  });

  save(): void {
    if (this.valid()) {
      this.ref.close({ quantity: this.quantity() ?? 1, note: this.note().trim() || null });
    }
  }
}
