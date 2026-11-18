import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  inject,
  input,
  signal,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MoneyApiService } from '../../core/api/money-api.service';

/**
 * The pictures a customer added to a claim in the app (T-438): small, in a
 * row; each opens full size in a new tab. Read from the panel's API only - an
 * object URL each, released with the component.
 */
@Component({
  selector: 'app-claim-photos',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatIconModule, TranslatePipe],
  template: `
    <div class="photos" [attr.aria-label]="'claims.photos' | translate: { count: count() }">
      @for (url of urls(); track $index) {
        @if (url) {
          <a [href]="url" target="_blank" rel="noopener" class="photos__item">
            <img [src]="url" [alt]="'claims.photo' | translate: { n: $index + 1 }" />
          </a>
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

  readonly claimId = input.required<string>();
  readonly count = input.required<number>();

  /** One per picture; null where it could not be read. */
  readonly urls = signal<(string | null)[]>([]);

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
