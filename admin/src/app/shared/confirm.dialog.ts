import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogModule,
  MatDialogRef,
} from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';

/** What the dialog is asked to confirm. */
export interface ConfirmRequest {
  /** What is about to happen. */
  readonly title: string;

  /** What the consequence is. Not a repeat of the title. */
  readonly explanation: string;

  /** The confirming button's text, e.g. "Delete". */
  readonly confirmLabel: string;

  readonly cancelLabel: string;

  /**
   * Whether this destroys something. A destructive action gets the danger
   * button and is required to collect a reason (plan section 8A.1).
   */
  readonly destructive?: boolean;

  /** The label above the reason box, when one is collected. */
  readonly reasonLabel?: string;

  /**
   * Collects a mandatory reason without the danger styling: undoing a block,
   * for instance, is audited but destroys nothing.
   */
  readonly requireReason?: boolean;
}

/** Nothing when it was cancelled; the reason (possibly empty) when confirmed. */
export type ConfirmResult = { readonly reason: string } | undefined;

/**
 * The one confirmation dialog in the panel.
 *
 * Replaces `window.confirm`, which cannot be translated, cannot collect the
 * reason the audit log needs, and does not look like the rest of the panel.
 */
@Component({
  selector: 'app-confirm-dialog',
  imports: [MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>

    <mat-dialog-content class="neo-dialog">
      <p class="neo-dialog__explanation">{{ data.explanation }}</p>

      @if (data.destructive || data.requireReason) {
        <mat-form-field class="neo-form__field">
          <mat-label>{{ data.reasonLabel }}</mat-label>
          <textarea
            matInput
            rows="3"
            required
            [ngModel]="reason()"
            (ngModelChange)="reason.set($event)"
          ></textarea>
        </mat-form-field>
      }
    </mat-dialog-content>

    <mat-dialog-actions class="neo-dialog__actions">
      <button mat-button type="button" (click)="cancel()">
        {{ data.cancelLabel }}
      </button>

      <button
        mat-flat-button
        type="button"
        [class.confirm--danger]="data.destructive"
        [disabled]="!canConfirm()"
        (click)="confirm()"
      >
        {{ data.confirmLabel }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    /* The design system's danger variant: a destructive confirm is red. */
    .confirm--danger {
      background: var(--danger-500);
      color: var(--neutral-0);
    }
  `,
})
export class ConfirmDialogComponent {
  readonly data = inject<ConfirmRequest>(MAT_DIALOG_DATA);

  private readonly dialogRef =
    inject<MatDialogRef<ConfirmDialogComponent, ConfirmResult>>(MatDialogRef);

  readonly reason = signal('');

  /** A destructive action needs its reason before it can be confirmed. */
  canConfirm(): boolean {
    return !(this.data.destructive || this.data.requireReason) || this.reason().trim().length > 0;
  }

  confirm(): void {
    if (!this.canConfirm()) {
      return;
    }

    this.dialogRef.close({ reason: this.reason().trim() });
  }

  cancel(): void {
    this.dialogRef.close(undefined);
  }
}

/**
 * Opens the confirmation dialog and resolves with the result.
 *
 * A function rather than a service so a component can `await` it without
 * subscribing, which is how the panel's other asynchronous calls read.
 */
export function confirmAction(dialog: MatDialog, request: ConfirmRequest): Promise<ConfirmResult> {
  return new Promise((resolve) => {
    dialog
      .open<ConfirmDialogComponent, ConfirmRequest, ConfirmResult>(ConfirmDialogComponent, {
        data: request,
        autoFocus: 'dialog',
        restoreFocus: true,
      })
      .afterClosed()
      .subscribe((result) => resolve(result));
  });
}
