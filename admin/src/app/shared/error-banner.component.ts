import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { TranslatePipe } from '@ngx-translate/core';

import { Problem } from './problem';

/**
 * A page-level failure: what happened, what it means for the page, and what
 * to do now (docs/DESIGN_SYSTEM.md section 4, "Xəta vəziyyəti").
 *
 * The technical part - the correlation id - is tucked under "Ətraflı", where
 * support can ask for it without it being in everybody's way.
 */
@Component({
  selector: 'app-error-banner',
  imports: [MatButtonModule, MatIconModule, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="banner" role="alert">
      <mat-icon class="banner__icon" aria-hidden="true">error</mat-icon>
      <div class="banner__text">
        <p class="banner__message">{{ problem().message | translate }}</p>
        @if (effect()) {
          <p class="banner__effect">{{ effect()! | translate }}</p>
        }
        @if (problem().correlationId) {
          <button
            mat-button
            type="button"
            class="banner__details"
            (click)="detailsOpen.set(!detailsOpen())"
          >
            {{ 'common.details' | translate }}
          </button>
          @if (detailsOpen()) {
            <p class="banner__correlation">
              {{ 'common.correlationId' | translate }}: <code>{{ problem().correlationId }}</code>
            </p>
          }
        }
      </div>
      @if (retryable()) {
        <button mat-stroked-button type="button" (click)="retry.emit()">
          {{ 'common.retry' | translate }}
        </button>
      }
    </div>
  `,
  styles: `
    .banner {
      display: flex;
      gap: var(--space-3);
      align-items: flex-start;
      padding: var(--space-3) var(--space-4);
      border: 1px solid var(--danger-500);
      border-radius: var(--radius-md);
      background: var(--danger-bg);
      color: var(--neutral-800);
    }

    .banner__icon {
      flex: none;
      color: var(--danger-500);
    }

    .banner__text {
      display: flex;
      flex: 1;
      flex-direction: column;
      gap: var(--space-1);
    }

    .banner__message {
      font-weight: var(--font-weight-medium);
    }

    .banner__effect,
    .banner__correlation {
      color: var(--neutral-600);
      font-size: var(--font-size-body-small);
    }

    .banner__details {
      align-self: flex-start;
    }
  `,
})
export class ErrorBannerComponent {
  readonly problem = input.required<Problem>();

  /** What the failure means for this page, as a translation key. */
  readonly effect = input<string | undefined>(undefined);

  readonly retryable = input(true);

  readonly retry = output<void>();

  readonly detailsOpen = signal(false);
}
