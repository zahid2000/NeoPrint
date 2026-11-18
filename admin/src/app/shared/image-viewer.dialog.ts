import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogModule,
  MatDialogRef,
} from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TranslatePipe } from '@ngx-translate/core';

export interface ImageViewerData {
  /** The pictures, as URLs the page already holds (object URLs). */
  readonly urls: readonly string[];
  /** Which one opens first. */
  readonly index: number;
  /** Translation key for each picture's name, with `{{n}}`. */
  readonly altKey: string;
}

/**
 * A picture looked at full size in a dialog (T-446), never in a new tab:
 * the panel stays where it was. With more than one picture, the arrows -
 * on screen or on the keyboard - go through them; Esc closes it.
 */
@Component({
  selector: 'app-image-viewer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatDialogModule, MatIconModule, MatTooltipModule, TranslatePipe],
  host: {
    '(document:keydown.arrowleft)': 'go(-1)',
    '(document:keydown.arrowright)': 'go(1)',
  },
  template: `
    <div class="viewer">
      <div class="viewer__bar">
        @if (data.urls.length > 1) {
          <span class="neo-caption">{{ index() + 1 }} / {{ data.urls.length }}</span>
        }
        <button
          mat-icon-button
          type="button"
          class="viewer__close"
          [attr.aria-label]="'common.close' | translate"
          [matTooltip]="'common.close' | translate"
          (click)="ref.close()"
        >
          <mat-icon>close</mat-icon>
        </button>
      </div>

      <div class="viewer__stage">
        @if (data.urls.length > 1) {
          <button
            mat-icon-button
            type="button"
            [attr.aria-label]="'common.back' | translate"
            [disabled]="index() === 0"
            (click)="go(-1)"
          >
            <mat-icon>chevron_left</mat-icon>
          </button>
        }
        <img [src]="url()" [alt]="data.altKey | translate: { n: index() + 1 }" />
        @if (data.urls.length > 1) {
          <button
            mat-icon-button
            type="button"
            [attr.aria-label]="'common.next' | translate"
            [disabled]="index() === data.urls.length - 1"
            (click)="go(1)"
          >
            <mat-icon>chevron_right</mat-icon>
          </button>
        }
      </div>
    </div>
  `,
  styles: `
    .viewer {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      padding: var(--space-2);
    }

    .viewer__bar {
      display: flex;
      align-items: center;
      gap: var(--space-2);
    }

    .viewer__close {
      margin-left: auto;
    }

    .viewer__stage {
      display: flex;
      align-items: center;
      justify-content: center;
      gap: var(--space-2);
    }

    .viewer__stage img {
      display: block;
      max-width: min(1200px, 80vw);
      max-height: 78vh;
      object-fit: contain;
      border-radius: var(--radius-sm);
      background: var(--neutral-50);
    }
  `,
})
export class ImageViewerDialog {
  readonly data = inject<ImageViewerData>(MAT_DIALOG_DATA);
  readonly ref = inject(MatDialogRef<ImageViewerDialog>);

  readonly index = signal(this.data.index);
  readonly url = computed(() => this.data.urls[this.index()]);

  go(step: number): void {
    const next = this.index() + step;
    if (next >= 0 && next < this.data.urls.length) {
      this.index.set(next);
    }
  }
}

/** Opens [data] in the viewer, sized to the picture. */
export function openImageViewer(dialog: MatDialog, data: ImageViewerData): void {
  dialog.open(ImageViewerDialog, {
    data,
    maxWidth: '95vw',
    autoFocus: false,
    panelClass: 'neo-image-viewer',
  });
}
