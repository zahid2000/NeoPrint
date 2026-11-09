import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService } from '../../core/api/admin-api.service';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { NotifyService } from '../../shared/notify.service';
import { Problem, toProblem } from '../../shared/problem';

/** The longest side of a kiosk picture after it is shrunk in the browser. */
export const KIOSK_PHOTO_MAX_SIDE = 1280;

/** JPEG quality of the shrunk picture: a card in the app, not a print. */
const JPEG_QUALITY = 0.85;

/**
 * Shrinks a picture to at most {@link KIOSK_PHOTO_MAX_SIDE} on its longest
 * side and re-encodes it as JPEG (T-340). Done here so the API process never
 * decodes an image (ADR-0005); re-encoding also drops whatever else the file
 * carried (location, camera data).
 */
export async function shrinkPhoto(file: File): Promise<Blob> {
  const bitmap = await createImageBitmap(file);
  try {
    const scale = Math.min(1, KIOSK_PHOTO_MAX_SIDE / Math.max(bitmap.width, bitmap.height));
    const width = Math.max(1, Math.round(bitmap.width * scale));
    const height = Math.max(1, Math.round(bitmap.height * scale));
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    const context = canvas.getContext('2d');
    if (!context) {
      throw new Error('No 2D canvas');
    }
    context.drawImage(bitmap, 0, 0, width, height);

    return await new Promise<Blob>((resolve, reject) =>
      canvas.toBlob(
        (blob) => (blob ? resolve(blob) : reject(new Error('Encoding failed'))),
        'image/jpeg',
        JPEG_QUALITY,
      ),
    );
  } finally {
    bitmap.close();
  }
}

/**
 * "Kiosk şəkli" on the kiosk's General tab (T-340; docs/DESIGN_SYSTEM.md,
 * "Admin: kiosk şəkli"): the picture the app shows on the kiosk's cards, a
 * way to choose a new one and to take it away.
 */
@Component({
  selector: 'app-kiosk-photo',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatIconModule, MatProgressSpinnerModule, TranslatePipe, ErrorBannerComponent],
  template: `
    <div class="photo">
      <div class="photo__frame">
        @if (objectUrl(); as src) {
          <img [src]="src" [alt]="'kioskPhoto.alt' | translate" class="photo__image" />
        } @else {
          <mat-icon class="photo__placeholder" aria-hidden="true">storefront</mat-icon>
        }
      </div>
      <div class="photo__body">
        <h3>{{ 'kioskPhoto.title' | translate }}</h3>
        <p class="neo-caption">{{ 'kioskPhoto.hint' | translate }}</p>
        @if (problem(); as current) {
          <app-error-banner
            [problem]="current"
            effect="kioskPhoto.failedEffect"
            [retryable]="false"
          />
        }
        @if (canEdit()) {
          <div class="photo__actions">
            <input
              #picker
              type="file"
              accept="image/jpeg,image/png,image/webp"
              hidden
              (change)="chosen(picker)"
            />
            <button mat-stroked-button type="button" [disabled]="busy()" (click)="picker.click()">
              @if (busy()) {
                <mat-spinner diameter="18" class="neo-button-spinner" />
              } @else {
                <mat-icon>add_photo_alternate</mat-icon>
              }
              {{ (photoUrl() ? 'kioskPhoto.replace' : 'kioskPhoto.upload') | translate }}
            </button>
            @if (photoUrl()) {
              <button mat-button type="button" class="photo__remove" [disabled]="busy()" (click)="remove()">
                <mat-icon>delete_outline</mat-icon>
                {{ 'kioskPhoto.remove' | translate }}
              </button>
            }
          </div>
        }
      </div>
    </div>
  `,
  styles: `
    .photo {
      display: flex;
      gap: var(--space-4);
      align-items: flex-start;
      flex-wrap: wrap;
    }

    .photo__frame {
      width: 160px;
      height: 120px;
      border-radius: var(--radius-md);
      background: var(--brand-50);
      display: flex;
      align-items: center;
      justify-content: center;
      overflow: hidden;
      flex: none;
    }

    .photo__image {
      width: 100%;
      height: 100%;
      object-fit: cover;
    }

    .photo__placeholder {
      color: var(--brand-500);
      font-size: var(--space-12);
      width: var(--space-12);
      height: var(--space-12);
    }

    .photo__body {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      flex: 1 1 240px;
    }

    .photo__body h3 {
      margin: 0;
    }

    .photo__actions {
      display: flex;
      gap: var(--space-2);
      flex-wrap: wrap;
    }

    .photo__remove {
      color: var(--danger-500);
    }
  `,
})
export class KioskPhotoComponent implements OnDestroy {
  private readonly api = inject(AdminApiService);
  private readonly notify = inject(NotifyService);

  readonly kioskId = input.required<string>();

  /** The detail's `photoUrl`; null without a picture. */
  readonly photoUrl = input<string | null | undefined>(null);
  readonly canEdit = input(false);

  /** After a new picture or a removal: the page reloads the kiosk. */
  readonly changed = output<void>();

  readonly objectUrl = signal<string | null>(null);
  readonly busy = signal(false);
  readonly problem = signal<Problem | null>(null);

  constructor() {
    // The picture needs the panel's token, so it is fetched, not linked.
    effect(() => {
      const url = this.photoUrl();
      this.release();
      if (url) {
        this.api.kioskPhoto(url).subscribe({
          next: (blob) => this.objectUrl.set(URL.createObjectURL(blob)),
          error: () => this.objectUrl.set(null),
        });
      }
    });
  }

  ngOnDestroy(): void {
    this.release();
  }

  async chosen(picker: HTMLInputElement): Promise<void> {
    const file = picker.files?.[0];
    picker.value = '';
    if (!file) {
      return;
    }

    this.busy.set(true);
    this.problem.set(null);
    try {
      let photo: Blob;
      try {
        photo = await shrinkPhoto(file);
      } catch {
        this.problem.set({ message: 'kioskPhoto.notImage' });
        return;
      }
      await firstValueFrom(this.api.setKioskPhoto(this.kioskId(), photo));
      this.notify.success('kioskPhoto.saved');
      this.changed.emit();
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.busy.set(false);
    }
  }

  async remove(): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      await firstValueFrom(this.api.removeKioskPhoto(this.kioskId()));
      this.notify.success('kioskPhoto.removed');
      this.changed.emit();
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.busy.set(false);
    }
  }

  private release(): void {
    const current = this.objectUrl();
    if (current) {
      URL.revokeObjectURL(current);
      this.objectUrl.set(null);
    }
  }
}
