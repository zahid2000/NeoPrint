import {
  CdkDrag,
  CdkDragDrop,
  CdkDragPlaceholder,
  CdkDropList,
  moveItemInArray,
} from '@angular/cdk/drag-drop';
import {
  ChangeDetectionStrategy,
  Component,
  Directive,
  computed,
  input,
  output,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TranslatePipe } from '@ngx-translate/core';

import { KioskScreenLayout } from '../../core/api/admin-api.service';
import { KIOSK_CHROME_TEXTS } from './kiosk-branding.defaults';

/** The kiosk's own arrangement, as it was before T-441. */
export const STANDARD_LAYOUT: KioskScreenLayout = {
  cards: ['usb', 'phone', 'code'],
  features: ['featureSizes', 'featureColor', 'featureFast', 'featureSafe'],
  imageLeft: false,
};

export const sameLayout = (a: KioskScreenLayout, b: KioskScreenLayout): boolean =>
  a.imageLeft === b.imageLeft &&
  a.cards.join() === b.cards.join() &&
  a.features.join() === b.features.join();

/** Every feature, the shown ones first in their order and the hidden ones after them. */
export const featureOrder = (layout: KioskScreenLayout): string[] => [
  ...layout.features,
  ...STANDARD_LAYOUT.features.filter((feature) => !layout.features.includes(feature)),
];

type Texts = Record<string, Record<string, string>>;

/** What a part of the preview needs: the texts and the language shown. */
@Directive()
abstract class PreviewPart {
  readonly texts = input.required<Texts>();
  readonly language = input.required<string>();

  text(key: string): string {
    return this.texts()[key]?.[this.language()] ?? '';
  }

  /** The kiosk's own words around the screen, `{0}` and `{1}` filled. */
  chrome(key: string, ...values: (string | number)[]): string {
    const words = KIOSK_CHROME_TEXTS[key]?.[this.language() as 'az' | 'en' | 'ru'] ?? '';
    return values.reduce<string>((text, value, i) => text.replace(`{${i}}`, String(value)), words);
  }
}

/** What can be dragged (T-441) says so, and where it lands shows as a dashed slot. */
const ARRANGING = `
  .editable .cdk-drag {
    cursor: grab;
    outline: 3px dashed transparent;
    outline-offset: var(--space-2);
    border-radius: var(--kiosk-radius-sm);
  }

  .editable .cdk-drag:hover {
    outline-color: var(--kiosk-brand-300);
  }

  .slot {
    flex: 1;
    border: 3px dashed var(--kiosk-brand-300);
    border-radius: var(--kiosk-radius-lg);
    background: var(--kiosk-brand-50);
  }

  .cdk-drag-animating,
  .cdk-drop-list-dragging .cdk-drag:not(.cdk-drag-placeholder) {
    transition: transform var(--motion-base) var(--motion-easing);
  }
`;

const CARD_ICONS: Record<string, string> = { usb: 'usb', phone: 'smartphone', code: 'qr_code_2' };

/**
 * The welcome screen in the preview (T-441): the big line and the picture,
 * the three ways in and "cancel by code". The picture and the cards are
 * dragged into place when `editable`.
 */
@Component({
  selector: 'app-kiosk-preview-home',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CdkDrag, CdkDragPlaceholder, CdkDropList, MatIconModule],
  template: `
    <div
      class="hero"
      [class.editable]="editable()"
      cdkDropList
      cdkDropListOrientation="horizontal"
      [cdkDropListDisabled]="!editable()"
      (cdkDropListDropped)="heroDropped($event)"
    >
      @for (part of heroParts(); track part) {
        @if (part === 'text') {
          <div class="hero__text" cdkDrag [cdkDragScale]="scale()">
            <p class="hero__title">{{ text('heroTitle') }}</p>
            <p class="muted">{{ text('heroSubtitle') }}</p>
            <div *cdkDragPlaceholder class="slot"></div>
          </div>
        } @else {
          <div class="hero__art" cdkDrag [cdkDragScale]="scale()" data-art>
            @if (imageUrl(); as src) {
              <img [src]="src" alt="" />
            } @else {
              <span class="hero__circle"></span>
              <span class="hero__paper"><mat-icon aria-hidden="true">description</mat-icon></span>
              <mat-icon class="hero__printer" aria-hidden="true">print</mat-icon>
            }
            <div *cdkDragPlaceholder class="slot slot--art"></div>
          </div>
        }
      }
    </div>

    <div
      class="cards"
      [class.editable]="editable()"
      cdkDropList
      cdkDropListOrientation="horizontal"
      [cdkDropListDisabled]="!editable()"
      (cdkDropListDropped)="cardDropped($event)"
    >
      @for (card of layout().cards; track card) {
        <div class="card" cdkDrag [cdkDragScale]="scale()" [attr.data-card]="card">
          <span class="tile"
            ><mat-icon aria-hidden="true">{{ cardIcon(card) }}</mat-icon></span
          >
          <strong class="card__title">{{ text(card + 'Title') }}</strong>
          <span class="muted">{{ text(card + 'Hint') }}</span>
          @if (card !== 'phone') {
            <mat-icon class="card__arrow" aria-hidden="true">arrow_forward</mat-icon>
          }
          <div *cdkDragPlaceholder class="slot"></div>
        </div>
      }
    </div>

    <span class="cancel">
      <mat-icon aria-hidden="true">info</mat-icon>
      {{ chrome('cancelByCode') }}
    </span>
  `,
  styles: [
    `
      :host {
        display: flex;
        flex-direction: column;
        gap: var(--space-6);
        padding: var(--space-8);
        min-width: 0;
      }

      p {
        margin: 0;
      }

      .muted {
        color: var(--kiosk-neutral-600);
      }

      mat-icon {
        flex: none;
      }

      .hero {
        display: flex;
        gap: var(--space-6);
        align-items: center;
      }

      .hero__text {
        display: flex;
        flex: 1;
        flex-direction: column;
        gap: var(--space-4);
        min-width: 0;
      }

      .hero__title {
        max-width: var(--kiosk-hero-text-max-width);
        font-size: var(--kiosk-font-size-hero);
        font-weight: 700;
        line-height: 1.15;
        overflow-wrap: anywhere;
      }

      .hero__art,
      .slot--art {
        position: relative;
        display: flex;
        flex: none;
        align-items: center;
        justify-content: center;
        width: var(--kiosk-hero-art-width);
        height: var(--kiosk-hero-art-height);
        border-radius: var(--kiosk-radius-lg);
        overflow: hidden;
      }

      .hero__art img {
        width: 100%;
        height: 100%;
        object-fit: cover;
        background: var(--kiosk-neutral-0);
      }

      .hero__circle {
        position: absolute;
        width: var(--kiosk-hero-circle-size);
        height: var(--kiosk-hero-circle-size);
        border-radius: 50%;
        background: var(--kiosk-brand-100);
      }

      .hero__paper {
        position: absolute;
        top: var(--space-12);
        padding: var(--space-4);
        border-radius: var(--kiosk-radius-sm);
        background: var(--kiosk-neutral-0);
        color: var(--kiosk-brand-300);
      }

      .hero__paper mat-icon {
        width: var(--kiosk-icon-paper);
        height: var(--kiosk-icon-paper);
        font-size: var(--kiosk-icon-paper);
      }

      .hero__printer {
        position: absolute;
        bottom: var(--space-6);
        width: var(--kiosk-icon-printer);
        height: var(--kiosk-icon-printer);
        font-size: var(--kiosk-icon-printer);
        color: var(--kiosk-brand-500);
      }

      .cards {
        display: grid;
        grid-template-columns: repeat(3, minmax(0, 1fr));
        gap: var(--space-6);
      }

      .card {
        display: flex;
        flex-direction: column;
        gap: var(--space-2);
        min-height: var(--kiosk-source-card-min-height);
        padding: var(--space-6);
        border: 2px solid var(--kiosk-neutral-200);
        border-radius: var(--kiosk-radius-lg);
        background: var(--kiosk-neutral-0);
        overflow-wrap: anywhere;
      }

      .tile {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        width: var(--kiosk-icon-tile-size);
        height: var(--kiosk-icon-tile-size);
        margin-bottom: var(--space-2);
        border-radius: var(--kiosk-radius-sm);
        background: var(--kiosk-brand-50);
        color: var(--kiosk-brand-500);
      }

      .tile mat-icon {
        width: var(--kiosk-icon-xl);
        height: var(--kiosk-icon-xl);
        font-size: var(--kiosk-icon-xl);
      }

      .card__title {
        font-size: var(--kiosk-font-size-h2);
        font-weight: var(--font-weight-semibold);
      }

      .card__arrow {
        align-self: flex-end;
        margin-top: auto;
        width: var(--kiosk-icon-lg);
        height: var(--kiosk-icon-lg);
        font-size: var(--kiosk-icon-lg);
        color: var(--kiosk-brand-500);
      }

      .cancel {
        display: inline-flex;
        gap: var(--space-4);
        align-items: center;
        min-height: var(--kiosk-touch-target);
        padding: 0 var(--space-4);
        color: var(--kiosk-neutral-900);
        font-size: var(--kiosk-font-size-body-small);
        font-weight: var(--font-weight-semibold);
      }

      .cancel mat-icon {
        width: var(--kiosk-icon-md);
        height: var(--kiosk-icon-md);
        font-size: var(--kiosk-icon-md);
      }
    `,
    ARRANGING,
  ],
})
export class KioskPreviewHomeComponent extends PreviewPart {
  readonly imageUrl = input<string | null>(null);
  readonly layout = input.required<KioskScreenLayout>();
  readonly editable = input(false);
  readonly scale = input(1);

  readonly layoutChange = output<KioskScreenLayout>();

  readonly heroParts = computed(() =>
    this.layout().imageLeft ? (['art', 'text'] as const) : (['text', 'art'] as const),
  );

  cardIcon(card: string): string {
    return CARD_ICONS[card] ?? 'help';
  }

  heroDropped(event: CdkDragDrop<unknown>): void {
    if (event.previousIndex !== event.currentIndex) {
      this.layoutChange.emit({ ...this.layout(), imageLeft: !this.layout().imageLeft });
    }
  }

  cardDropped(event: CdkDragDrop<unknown>): void {
    const cards = [...this.layout().cards];
    moveItemInArray(cards, event.previousIndex, event.currentIndex);
    this.layoutChange.emit({ ...this.layout(), cards });
  }
}

/** The queue panel beside the screen (T-389): one order printing, one waiting - an example. */
@Component({
  selector: 'app-kiosk-preview-queue',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatIconModule],
  template: `
    <div class="title">
      <mat-icon aria-hidden="true">format_list_numbered</mat-icon>
      <strong>{{ chrome('queueTitle') }}</strong>
    </div>
    <div class="row row--printing">
      <span class="place">#1</span>
      <span class="body">
        <strong>482913</strong>
        <span class="caption">{{ chrome('pages', 12) }}</span>
        <span class="status">
          <mat-icon class="ok" aria-hidden="true">hourglass_top</mat-icon>
          {{ chrome('statusPrinting') }}
        </span>
        <span class="progress">{{ chrome('progress', 3, 12) }}</span>
        <span class="bar"><span></span></span>
        <span class="caption">{{ chrome('finishAt', soon()) }}</span>
      </span>
    </div>
    <div class="row">
      <span class="place">#2</span>
      <span class="body">
        <strong>775201</strong>
        <span class="caption">{{ chrome('pages', 4) }}</span>
        <span class="status">
          <mat-icon class="wait" aria-hidden="true">schedule</mat-icon>
          {{ chrome('statusQueued') }}
        </span>
        <span class="caption">{{ chrome('finishAt', finishAt()) }}</span>
      </span>
    </div>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--space-4);
      padding: var(--space-6);
      border-left: 1px solid var(--kiosk-neutral-200);
      background: var(--kiosk-neutral-0);
    }

    .title {
      display: flex;
      gap: var(--space-2);
      align-items: center;
      font-size: var(--kiosk-font-size-h2);
    }

    .title mat-icon {
      color: var(--kiosk-brand-600);
    }

    mat-icon {
      flex: none;
      width: var(--kiosk-icon-sm);
      height: var(--kiosk-icon-sm);
      font-size: var(--kiosk-icon-sm);
    }

    .row {
      display: flex;
      gap: var(--space-4);
      padding: var(--space-4);
      border: 1px solid var(--kiosk-neutral-200);
      border-radius: var(--kiosk-radius-sm);
      background: var(--kiosk-neutral-0);
      font-size: var(--kiosk-font-size-body-small);
    }

    .row--printing {
      border-color: var(--kiosk-brand-300);
      background: var(--kiosk-brand-50);
    }

    .place {
      color: var(--kiosk-brand-600);
      font-weight: 700;
    }

    .body {
      display: flex;
      flex: 1;
      flex-direction: column;
      gap: var(--space-2);
      min-width: 0;
    }

    .caption {
      color: var(--kiosk-neutral-600);
      font-size: var(--kiosk-font-size-caption);
    }

    .status {
      display: inline-flex;
      gap: var(--space-2);
      align-items: center;
      font-size: var(--kiosk-font-size-caption);
      font-weight: var(--font-weight-semibold);
    }

    .ok {
      color: var(--kiosk-success-500);
    }

    .wait {
      color: var(--kiosk-warning-500);
    }

    .progress {
      color: var(--kiosk-brand-600);
      font-weight: var(--font-weight-semibold);
    }

    .bar {
      display: block;
      height: var(--kiosk-progress-height);
      border-radius: var(--kiosk-progress-height);
      background: var(--kiosk-neutral-100);
      overflow: hidden;
    }

    .bar span {
      display: block;
      width: 25%;
      height: 100%;
      background: var(--kiosk-brand-600);
    }
  `,
})
export class KioskPreviewQueueComponent extends PreviewPart {
  readonly soon = input('');
  readonly finishAt = input('');
}

const FEATURE_ICONS: Record<string, string> = {
  featureSizes: 'description',
  featureColor: 'contrast',
  featureFast: 'bolt',
  featureSafe: 'shield',
};

/**
 * The footer (T-371, T-420): the QR and the code, the help line, what the
 * kiosk offers and the printer. The features are dragged into their order,
 * and hidden or shown again, when `editable`.
 */
@Component({
  selector: 'app-kiosk-preview-footer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    CdkDrag,
    CdkDragPlaceholder,
    CdkDropList,
    MatIconModule,
    MatTooltipModule,
    TranslatePipe,
  ],
  template: `
    <span class="start">
      <span class="qr"><mat-icon aria-hidden="true">qr_code_2</mat-icon></span>
      <strong>K-1042</strong>
      @if (text('hotline'); as hotline) {
        <span class="hotline"><mat-icon aria-hidden="true">call</mat-icon>{{ hotline }}</span>
      }
    </span>
    <div
      class="features"
      [class.editable]="editable()"
      cdkDropList
      cdkDropListOrientation="horizontal"
      [cdkDropListDisabled]="!editable()"
      (cdkDropListDropped)="dropped($event)"
    >
      @for (feature of features(); track feature) {
        <span
          class="feature"
          cdkDrag
          [cdkDragScale]="scale()"
          [class.feature--hidden]="!shown(feature)"
          [attr.data-feature]="feature"
        >
          <mat-icon class="feature__icon" aria-hidden="true">{{ icon(feature) }}</mat-icon>
          <span class="words">
            <strong>{{ text(feature) }}</strong>
            <span>{{ text(feature + 'Hint') }}</span>
          </span>
          @if (editable()) {
            <button
              type="button"
              class="eye"
              [attr.aria-label]="eyeLabel(feature) | translate"
              [matTooltip]="eyeLabel(feature) | translate"
              (click)="toggle(feature)"
            >
              <mat-icon aria-hidden="true">{{
                shown(feature) ? 'visibility' : 'visibility_off'
              }}</mat-icon>
            </button>
          }
          <div *cdkDragPlaceholder class="slot"></div>
        </span>
      }
    </div>
    <span class="chip">
      <mat-icon aria-hidden="true">check_circle</mat-icon>
      {{ chrome('ready') }}
    </span>
  `,
  styles: [
    `
      :host {
        display: flex;
        gap: var(--space-6);
        align-items: center;
        padding: 0 var(--space-8);
        border-top: 1px solid var(--kiosk-neutral-200);
        background: var(--kiosk-neutral-0);
        font-size: var(--kiosk-font-size-body-small);
      }

      mat-icon {
        flex: none;
        width: var(--kiosk-icon-sm);
        height: var(--kiosk-icon-sm);
        font-size: var(--kiosk-icon-sm);
      }

      .start {
        display: inline-flex;
        flex: none;
        gap: var(--space-4);
        align-items: center;
      }

      .qr {
        display: inline-flex;
        padding: var(--space-1);
        border-radius: var(--kiosk-radius-sm);
        background: var(--qr-background);
      }

      .qr mat-icon {
        width: var(--kiosk-qr-size);
        height: var(--kiosk-qr-size);
        font-size: var(--kiosk-qr-size);
      }

      .hotline {
        display: inline-flex;
        gap: var(--space-2);
        align-items: center;
        margin-left: var(--space-6);
        color: var(--kiosk-danger-500);
        font-weight: 700;
      }

      .features {
        display: flex;
        flex: 1;
        gap: var(--space-4);
        min-width: 0;
      }

      .feature {
        display: flex;
        flex: 1 1 0;
        gap: var(--space-2);
        align-items: center;
        min-width: 0;
        min-height: var(--kiosk-touch-target);
        font-size: var(--kiosk-font-size-caption);
      }

      .feature__icon {
        width: var(--kiosk-icon-md);
        height: var(--kiosk-icon-md);
        font-size: var(--kiosk-icon-md);
        color: var(--kiosk-brand-500);
      }

      .words {
        display: flex;
        flex-direction: column;
        min-width: 0;
      }

      .words > * {
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }

      .words span {
        color: var(--kiosk-neutral-600);
      }

      .feature--hidden {
        opacity: 0.35;
      }

      .eye {
        display: inline-flex;
        flex: none;
        align-items: center;
        justify-content: center;
        width: var(--kiosk-touch-target);
        height: var(--kiosk-touch-target);
        margin-left: auto;
        border: 1px solid var(--kiosk-neutral-200);
        border-radius: 50%;
        background: var(--kiosk-neutral-0);
        color: var(--kiosk-brand-600);
        cursor: pointer;
      }

      .eye mat-icon {
        width: var(--kiosk-icon-lg);
        height: var(--kiosk-icon-lg);
        font-size: var(--kiosk-icon-lg);
      }

      .chip {
        display: inline-flex;
        flex: none;
        gap: var(--space-2);
        align-items: center;
        min-height: var(--kiosk-status-chip-height);
        padding: 0 var(--space-6);
        border-radius: var(--kiosk-radius-sm);
        background: var(--kiosk-success-bg);
        font-weight: var(--font-weight-semibold);
      }

      .chip mat-icon {
        color: var(--kiosk-success-500);
      }
    `,
    ARRANGING,
  ],
})
export class KioskPreviewFooterComponent extends PreviewPart {
  readonly layout = input.required<KioskScreenLayout>();
  readonly editable = input(false);
  readonly scale = input(1);

  readonly layoutChange = output<KioskScreenLayout>();

  /** On the kiosk only the shown ones; while arranging, the hidden ones too, faded. */
  readonly features = computed(() =>
    this.editable() ? featureOrder(this.layout()) : this.layout().features,
  );

  shown(feature: string): boolean {
    return this.layout().features.includes(feature);
  }

  icon(feature: string): string {
    return FEATURE_ICONS[feature] ?? 'help';
  }

  eyeLabel(feature: string): string {
    return this.shown(feature) ? 'kioskBranding.hideFeature' : 'kioskBranding.showFeature';
  }

  /** The order of every feature changes; the hidden ones stay hidden. */
  dropped(event: CdkDragDrop<unknown>): void {
    const order = this.features().slice();
    moveItemInArray(order, event.previousIndex, event.currentIndex);
    this.layoutChange.emit({
      ...this.layout(),
      features: order.filter((feature) => this.shown(feature)),
    });
  }

  /** Hidden, it leaves the kiosk's footer; shown again, it comes back where it stands now. */
  toggle(feature: string): void {
    const shown = this.layout().features;
    const next = this.shown(feature)
      ? shown.filter((item) => item !== feature)
      : featureOrder(this.layout()).filter((item) => item === feature || shown.includes(item));
    this.layoutChange.emit({ ...this.layout(), features: next });
  }
}
