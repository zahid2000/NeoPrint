import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { TranslatePipe } from '@ngx-translate/core';

/**
 * How a kiosk's welcome screen looks with the texts and picture being edited
 * (T-422): the kiosk's current design, small - header, the big line with the
 * picture beside it, the three ways in, and the footer with its four
 * features and the help line. It follows every keystroke; the language is
 * chosen above it.
 */
@Component({
  selector: 'app-kiosk-screen-preview',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonToggleModule, MatIconModule, TranslatePipe],
  template: `
    <div class="preview__bar">
      <h3>{{ 'kioskBranding.preview' | translate }}</h3>
      <mat-button-toggle-group
        hideSingleSelectionIndicator
        [value]="language()"
        (change)="language.set($event.value)"
        [attr.aria-label]="'kioskBranding.previewLanguage' | translate"
      >
        @for (code of languages; track code) {
          <mat-button-toggle [value]="code">{{ code.toUpperCase() }}</mat-button-toggle>
        }
      </mat-button-toggle-group>
    </div>

    <div class="screen" role="img" [attr.aria-label]="'kioskBranding.preview' | translate">
      <div class="screen__header">
        <span class="screen__logo"><mat-icon aria-hidden="true">print</mat-icon></span>
        <strong>NeoPrint</strong>
      </div>

      <div class="screen__hero">
        <div>
          <p class="screen__title">{{ text('heroTitle') }}</p>
          <p class="screen__subtitle">{{ text('heroSubtitle') }}</p>
        </div>
        <div class="screen__art">
          @if (imageUrl(); as src) {
            <img [src]="src" alt="" />
          } @else {
            <mat-icon aria-hidden="true">print</mat-icon>
          }
        </div>
      </div>

      <div class="screen__cards">
        @for (card of cards; track card.key) {
          <div class="screen__card">
            <mat-icon aria-hidden="true">{{ card.icon }}</mat-icon>
            <strong>{{ text(card.key + 'Title') }}</strong>
            <span>{{ text(card.key + 'Hint') }}</span>
          </div>
        }
      </div>

      <div class="screen__footer">
        @if (text('hotline'); as hotline) {
          <span class="screen__hotline"
            ><mat-icon aria-hidden="true">call</mat-icon>{{ hotline }}</span
          >
        }
        @for (feature of features; track feature) {
          <span class="screen__feature">
            <strong>{{ text(feature) }}</strong>
            <span>{{ text(feature + 'Hint') }}</span>
          </span>
        }
      </div>
    </div>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
    }

    .preview__bar {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      align-items: center;
      justify-content: space-between;

      h3 {
        margin: 0;
      }
    }

    .screen {
      display: flex;
      flex-direction: column;
      gap: var(--space-3);
      padding: var(--space-3);
      border: 1px solid var(--neutral-200);
      border-radius: var(--radius-md);
      background: var(--brand-50);
      font-size: var(--font-size-caption);
      overflow: hidden;
    }

    .screen__header {
      display: flex;
      gap: var(--space-2);
      align-items: center;
    }

    // Icons at the panel's own size; the picture's stand-in larger.
    mat-icon {
      width: var(--icon-size);
      height: var(--icon-size);
      font-size: var(--icon-size);
    }

    .screen__art mat-icon {
      width: var(--font-size-display);
      height: var(--font-size-display);
      font-size: var(--font-size-display);
    }

    .screen__logo {
      display: inline-flex;
      padding: var(--space-1);
      border-radius: var(--radius-sm);
      background: var(--brand-500);
      color: var(--neutral-0);
    }

    .screen__hero {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 30%;
      gap: var(--space-3);
      align-items: center;
    }

    .screen__title {
      margin: 0;
      font-size: var(--font-size-body);
      font-weight: var(--font-weight-semibold);
      line-height: 1.2;
      overflow-wrap: anywhere;
    }

    .screen__subtitle {
      margin: var(--space-1) 0 0;
      color: var(--neutral-600);
    }

    // The kiosk's card the picture fills (T-421).
    .screen__art {
      display: flex;
      align-items: center;
      justify-content: center;
      aspect-ratio: 4 / 3;
      border-radius: var(--radius-md);
      background: var(--neutral-0);
      overflow: hidden;
      color: var(--brand-300);

      img {
        width: 100%;
        height: 100%;
        object-fit: cover;
      }
    }

    .screen__cards {
      display: grid;
      grid-template-columns: repeat(3, minmax(0, 1fr));
      gap: var(--space-2);
    }

    .screen__card {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
      padding: var(--space-2);
      border-radius: var(--radius-sm);
      background: var(--neutral-0);
      overflow-wrap: anywhere;

      mat-icon {
        color: var(--brand-500);
      }

      span {
        color: var(--neutral-600);
      }
    }

    .screen__footer {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2) var(--space-4);
      padding-top: var(--space-2);
      border-top: 1px solid var(--neutral-200);
    }

    .screen__feature {
      display: flex;
      flex-direction: column;

      span {
        color: var(--neutral-600);
      }
    }

    // The help line: red, bold, behind a phone (T-420).
    .screen__hotline {
      display: inline-flex;
      gap: var(--space-1);
      align-items: center;
      color: var(--danger-500);
      font-weight: var(--font-weight-semibold);
    }
  `,
})
export class KioskScreenPreviewComponent {
  /** What each text says now, by key and language - typed, default or the kiosk's own. */
  readonly texts = input.required<Record<string, Record<string, string>>>();
  readonly imageUrl = input<string | null>(null);

  readonly languages = ['az', 'en', 'ru'] as const;
  readonly language = signal<string>('az');

  readonly cards = [
    { key: 'usb', icon: 'usb' },
    { key: 'phone', icon: 'qr_code_2' },
    { key: 'code', icon: 'pin' },
  ] as const;
  readonly features = ['featureSizes', 'featureColor', 'featureFast', 'featureSafe'] as const;

  private readonly current = computed(() => this.texts());

  text(key: string): string {
    return this.current()[key]?.[this.language()] ?? '';
  }
}
