import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { TranslatePipe } from '@ngx-translate/core';

import { KioskScreenLayout } from '../../core/api/admin-api.service';
import {
  KioskPreviewFooterComponent,
  KioskPreviewHomeComponent,
  KioskPreviewQueueComponent,
  STANDARD_LAYOUT,
} from './kiosk-screen-preview.parts';

export { STANDARD_LAYOUT, featureOrder, sameLayout } from './kiosk-screen-preview.parts';

/** The kiosk's 1920x1080 design (desktop Tokens.axaml DesignWidth / DesignHeight). */
const DESIGN_WIDTH = 1920;
const DESIGN_HEIGHT = 1080;

const pad = (value: number): string => value.toString().padStart(2, '0');
const time = (value: Date): string => `${pad(value.getHours())}:${pad(value.getMinutes())}`;

/**
 * The kiosk's welcome screen as the kiosk draws it (T-422, T-441): the header,
 * the welcome screen, the queue panel beside it and the footer - at the
 * kiosk's own 1920x1080 and scaled to fit, so it is the kiosk's screen, only
 * smaller. It follows every keystroke; the language is chosen above it; it
 * opens full screen. With `editable`, the ways in, the picture and the
 * footer's features are dragged into place.
 */
@Component({
  selector: 'app-kiosk-screen-preview',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    KioskPreviewFooterComponent,
    KioskPreviewHomeComponent,
    KioskPreviewQueueComponent,
    MatButtonModule,
    MatButtonToggleModule,
    MatIconModule,
    TranslatePipe,
  ],
  template: `
    <div class="bar">
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
      <button mat-stroked-button type="button" (click)="toggleFullscreen()">
        <mat-icon>fullscreen</mat-icon>
        {{ 'kioskBranding.fullscreen' | translate }}
      </button>
    </div>
    @if (editable()) {
      <p class="neo-caption hint">
        <mat-icon aria-hidden="true">pan_tool</mat-icon>
        {{ 'kioskBranding.dragHint' | translate }}
      </p>
    }

    <div #stage class="stage" [class.stage--full]="fullscreen()">
      @if (fullscreen()) {
        <button mat-flat-button type="button" class="exit" (click)="toggleFullscreen()">
          <mat-icon>fullscreen_exit</mat-icon>
          {{ 'kioskBranding.exitFullscreen' | translate }}
        </button>
      }
      <div
        class="surface"
        role="img"
        [attr.aria-label]="'kioskBranding.preview' | translate"
        [style.transform]="'scale(' + scale() + ')'"
      >
        <!-- The mark and the name, the kiosk's name; "AZ ▾", the clock and the date. -->
        <div class="header">
          <span class="logo"><mat-icon aria-hidden="true">print</mat-icon></span>
          <strong class="brand">NeoPrint</strong>
          <span class="name">{{ kioskName() ?? '' }}</span>
          <span class="lang">
            <mat-icon aria-hidden="true">language</mat-icon>
            {{ language().toUpperCase() }}
            <mat-icon aria-hidden="true">expand_more</mat-icon>
          </span>
          <span class="clock">
            <strong>{{ clock() }}</strong>
            <span>{{ date() }}</span>
          </span>
        </div>

        <div class="middle">
          <app-kiosk-preview-home
            [texts]="texts()"
            [language]="language()"
            [imageUrl]="imageUrl()"
            [layout]="layout()"
            [editable]="editable()"
            [scale]="scale()"
            (layoutChange)="layoutChange.emit($event)"
          />
          <app-kiosk-preview-queue
            [texts]="texts()"
            [language]="language()"
            [soon]="soon()"
            [finishAt]="later()"
          />
        </div>

        <app-kiosk-preview-footer
          [texts]="texts()"
          [language]="language()"
          [layout]="layout()"
          [editable]="editable()"
          [scale]="scale()"
          (layoutChange)="layoutChange.emit($event)"
        />
      </div>
    </div>
    <p class="neo-caption note">{{ 'kioskBranding.previewNote' | translate }}</p>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
    }

    .bar {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      align-items: center;
    }

    .bar h3 {
      margin: 0 auto 0 0;
    }

    .hint,
    .note {
      display: flex;
      gap: var(--space-1);
      align-items: center;
      margin: 0;
    }

    .hint mat-icon {
      width: var(--icon-size);
      height: var(--icon-size);
      font-size: var(--icon-size);
    }

    // The kiosk's screen, 16:9, with the 1920x1080 surface scaled into it.
    .stage {
      position: relative;
      width: 100%;
      aspect-ratio: 16 / 9;
      overflow: hidden;
      border: 1px solid var(--neutral-200);
      border-radius: var(--radius-md);
      background: var(--kiosk-neutral-0);
    }

    .stage--full {
      display: flex;
      align-items: center;
      justify-content: center;
      border: 0;
      border-radius: 0;
      background: var(--kiosk-neutral-900);
    }

    .exit {
      position: absolute;
      top: var(--space-4);
      right: var(--space-4);
      z-index: 1;
    }

    .surface {
      position: absolute;
      top: 0;
      left: 0;
      display: grid;
      grid-template-rows: var(--kiosk-header-height) minmax(0, 1fr) var(--kiosk-footer-height);
      width: var(--kiosk-design-width);
      height: var(--kiosk-design-height);
      transform-origin: top left;
      background: linear-gradient(var(--kiosk-brand-50), var(--kiosk-neutral-0) 70%);
      color: var(--kiosk-neutral-900);
      font-size: var(--kiosk-font-size-body);
      line-height: 1.25;
    }

    .stage--full .surface {
      position: static;
      flex: none;
      transform-origin: center;
    }

    mat-icon {
      flex: none;
    }

    .surface mat-icon {
      width: var(--kiosk-icon-sm);
      height: var(--kiosk-icon-sm);
      font-size: var(--kiosk-icon-sm);
    }

    .header {
      display: flex;
      gap: var(--space-4);
      align-items: center;
      padding: 0 var(--space-8);
    }

    .logo {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: var(--kiosk-logo-size);
      height: var(--kiosk-logo-size);
      border-radius: var(--kiosk-radius-sm);
      background: var(--kiosk-brand-500);
      color: var(--kiosk-neutral-0);
    }

    .surface .logo mat-icon {
      width: var(--kiosk-icon-md);
      height: var(--kiosk-icon-md);
      font-size: var(--kiosk-icon-md);
    }

    .brand {
      font-size: var(--kiosk-font-size-h1);
    }

    .name {
      flex: 1;
      margin-left: var(--space-8);
      overflow: hidden;
      color: var(--kiosk-neutral-600);
      white-space: nowrap;
      text-overflow: ellipsis;
    }

    .lang {
      display: inline-flex;
      gap: var(--space-2);
      align-items: center;
      min-height: var(--kiosk-touch-target);
      padding: 0 var(--space-6);
      border: 1px solid var(--kiosk-neutral-200);
      border-radius: var(--kiosk-radius-sm);
      background: var(--kiosk-neutral-0);
      font-size: var(--kiosk-font-size-body-small);
      font-weight: var(--font-weight-semibold);
    }

    .clock {
      display: flex;
      flex-direction: column;
      align-items: flex-end;
      margin-left: var(--space-8);
    }

    .clock strong {
      font-size: var(--kiosk-font-size-h2);
    }

    .clock span {
      color: var(--kiosk-neutral-600);
      font-size: var(--kiosk-font-size-caption);
    }

    .middle {
      display: grid;
      grid-template-columns: minmax(0, 1fr) var(--kiosk-queue-width);
      min-height: 0;
    }
  `,
})
export class KioskScreenPreviewComponent {
  private readonly destroyRef = inject(DestroyRef);

  /** What each text says now, by key and language - typed, default or the kiosk's own. */
  readonly texts = input.required<Record<string, Record<string, string>>>();
  readonly imageUrl = input<string | null>(null);
  readonly layout = input<KioskScreenLayout>(STANDARD_LAYOUT);
  /** The arrangement can be dragged (T-441). */
  readonly editable = input(false);
  /** One kiosk's name in the header; empty for the default. */
  readonly kioskName = input<string | null>(null);

  readonly layoutChange = output<KioskScreenLayout>();

  readonly languages = ['az', 'en', 'ru'] as const;
  readonly language = signal<string>('az');
  readonly fullscreen = signal(false);
  readonly scale = signal(0.5);
  readonly now = signal(new Date());

  private readonly stage = viewChild.required<ElementRef<HTMLElement>>('stage');

  readonly clock = computed(() => time(this.now()));
  readonly soon = computed(() => time(new Date(this.now().getTime() + 2 * 60_000)));
  readonly later = computed(() => time(new Date(this.now().getTime() + 6 * 60_000)));
  readonly date = computed(() => {
    const now = this.now();
    return `${pad(now.getDate())}.${pad(now.getMonth() + 1)}.${now.getFullYear()}`;
  });

  constructor() {
    // Everything started below is stopped here - registered first, so a
    // preview destroyed before its first render leaves no timer behind (a
    // live interval kept the panel's test run from ever ending).
    let destroyed = false;
    const stops: (() => void)[] = [];
    this.destroyRef.onDestroy(() => {
      destroyed = true;
      stops.forEach((stop) => stop());
    });

    afterNextRender(() => {
      // Without ResizeObserver (a test's DOM) there is no size to fit to:
      // nothing is started, so nothing can outlive the preview.
      if (destroyed || typeof ResizeObserver === 'undefined') {
        return;
      }

      const stage = this.stage().nativeElement;
      const fit = () => {
        const { width, height } = stage.getBoundingClientRect();
        if (width > 0 && height > 0) {
          this.scale.set(Math.min(width / DESIGN_WIDTH, height / DESIGN_HEIGHT));
        }
      };
      const observer = new ResizeObserver(fit);
      observer.observe(stage);
      stops.push(() => observer.disconnect());
      fit();

      const onFullscreen = () => this.fullscreen.set(document.fullscreenElement === stage);
      document.addEventListener('fullscreenchange', onFullscreen);
      stops.push(() => document.removeEventListener('fullscreenchange', onFullscreen));

      // A chain of timeouts, not an interval: each step checks the preview is
      // still on the page, so a timer never outlives it whatever destroys it.
      let tick: ReturnType<typeof setTimeout> | undefined;
      const step = () => {
        if (destroyed || !stage.isConnected) {
          return;
        }
        this.now.set(new Date());
        tick = setTimeout(step, 30_000);
      };
      tick = setTimeout(step, 30_000);
      stops.push(() => clearTimeout(tick));
    });
  }

  async toggleFullscreen(): Promise<void> {
    const stage = this.stage().nativeElement;
    if (document.fullscreenElement) {
      await document.exitFullscreen();
    } else if (stage.requestFullscreen) {
      await stage.requestFullscreen();
    }
  }
}
