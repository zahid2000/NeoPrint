import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  inject,
  input,
  signal,
} from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MoneyApiService } from '../../core/api/money-api.service';
import { openImageViewer } from '../../shared/image-viewer.dialog';

/**
 * The pictures a customer added to a claim in the app (T-438): small, in a
 * row; each opens full size in the panel's picture viewer (T-446), never in
 * a new tab. Read from the panel's API only - an object URL each, released
 * with the component.
 */
@Component({
  selector: 'app-claim-photos',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatIconModule, TranslatePipe],
  template: `
    <div class="photos" [attr.aria-label]="'claims.photos' | translate: { count: count() }">
      @for (url of urls(); track $index) {
        @if (url) {
          <button
            type="button"
            class="photos__item"
            [attr.aria-label]="'claims.openPhoto' | translate: { n: $index + 1 }"
            (click)="open($index)"
          >
            <img [src]="url" [alt]="'claims.photo' | translate: { n: $index + 1 }" />
          </button>
        } @else {
          <span class="photos__item photos__item--missing">
            <mat-icon aria-hidden="true">broken_image</mat-icon>
          </span>
        }
      }
    </div>
  `,
  styles: `
    .photos {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
    }

    .photos__item {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: var(--control-height-lg);
      height: var(--control-height-lg);
      overflow: hidden;
      border: 1px solid var(--neutral-200);
      border-radius: var(--radius-sm);
      background: var(--neutral-50);
      color: var(--neutral-600);
    }

    button.photos__item {
      padding: 0;
      cursor: zoom-in;
    }

    button.photos__item:focus-visible {
      outline: 2px solid var(--focus-ring);
      outline-offset: 2px;
    }

    .photos__item img {
      width: 100%;
      height: 100%;
      object-fit: cover;
    }
  `,
})
export class ClaimPhotosComponent implements OnInit {
  private readonly api = inject(MoneyApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly dialog = inject(MatDialog);

  readonly claimId = input.required<string>();
  readonly count = input.required<number>();

  /** One per picture; null where it could not be read. */
  readonly urls = signal<(string | null)[]>([]);

  /** The viewer on this picture; the arrows go through the ones that loaded. */
  open(index: number): void {
    const all = this.urls();
    const loaded = all.filter((url): url is string => url !== null);
    const at = loaded.indexOf(all[index] ?? '');
    if (at >= 0) {
      openImageViewer(this.dialog, { urls: loaded, index: at, altKey: 'claims.photo' });
    }
  }

  async ngOnInit(): Promise<void> {
    const made: string[] = [];
    this.destroyRef.onDestroy(() => made.forEach((url) => URL.revokeObjectURL(url)));

    const urls: (string | null)[] = [];
    for (let index = 0; index < this.count(); index++) {
      try {
        const url = URL.createObjectURL(
          await firstValueFrom(this.api.claimPhoto(this.claimId(), index)),
        );
        made.push(url);
        urls.push(url);
      } catch {
        urls.push(null);
      }
    }
    this.urls.set(urls);
  }
}
