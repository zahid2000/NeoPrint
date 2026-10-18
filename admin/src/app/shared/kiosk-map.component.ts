import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { TranslatePipe } from '@ngx-translate/core';

import { ThemeService } from '../core/theme/theme.service';
import { EngineMarker, MAP_ENGINE, MapAttributionLink, MapConfigService, MapHandle, MapPoint } from '../core/map/map-engine';
import { StatusTone } from './status-chip.component';

/** One point on the map. */
export interface MapMarker {
  readonly id: string;
  readonly lat: number;
  readonly lng: number;

  /** Shown on click: plain text, never HTML. */
  readonly title: string;
  readonly subtitle?: string;
  readonly tone: StatusTone;
}

/** Baku: where the service starts, and where an empty map opens. */
const defaultCenter: MapPoint = { lat: 40.4093, lng: 49.8671 };

/** How long the style may take before the map is given up on (T-286). */
export const mapLoadTimeoutMs = 8000;

const toneTokens: Record<StatusTone, string> = {
  success: '--success-500',
  warning: '--warning-500',
  danger: '--danger-500',
  info: '--info-500',
  neutral: '--neutral-400',
};

type MapState = 'loading' | 'ready' | 'failed';

/**
 * Kiosks on the OpenFreeMap map (MapLibre), or a point picker for the kiosk
 * form (docs/DESIGN_SYSTEM.md section 4, "Xəritə"; T-217).
 *
 * The style and the attribution are the server's settings. The attribution is
 * always under the map, every part a link, and cannot be folded away. A map
 * that does not load in {@link mapLoadTimeoutMs} gives way to a notice with a
 * retry - never an empty box - and each new map tries again.
 *
 * Markers are circles in the colour of their status tone (a design token, so
 * the theme applies). Colour is never the only signal: the page shows a legend
 * with icons and words, and a marker's popup names the kiosk.
 */
@Component({
  selector: 'app-kiosk-map',
  imports: [MatButtonModule, MatIconModule, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (state() === 'failed') {
      <div class="neo-notice map__failed" role="status">
        <mat-icon aria-hidden="true">map</mat-icon>
        <span>
          <strong>{{ 'map.failedTitle' | translate }}</strong>
          {{ (picking() ? 'map.failedPick' : 'map.failedList') | translate }}
        </span>
        <button mat-stroked-button type="button" (click)="retry()">
          <mat-icon aria-hidden="true">refresh</mat-icon>
          {{ 'common.retry' | translate }}
        </button>
      </div>
    }

    <div
      #host
      class="map"
      [class.map--hidden]="state() === 'failed'"
      [attr.aria-busy]="state() === 'loading'"
      role="application"
      [attr.aria-label]="label()"
    ></div>

    @if (state() !== 'failed' && attribution().length > 0) {
      <p class="neo-caption map__attribution" data-map-attribution>
        @for (link of attribution(); track link.url; let last = $last) {
          <a [href]="link.url" target="_blank" rel="noopener noreferrer">{{ link.label }}</a>
          @if (!last) {
            <span aria-hidden="true"> · </span>
          }
        }
      </p>
    }
  `,
  styles: `
    :host {
      display: block;
    }

    .map {
      height: var(--map-height, 480px);
      border: 1px solid var(--neutral-200);
      border-radius: var(--radius-md);
      overflow: hidden;
      background: var(--neutral-50);
    }

    .map--hidden {
      display: none;
    }

    .map__failed {
      background: var(--warning-bg);

      > mat-icon {
        color: var(--warning-500);
      }
    }

    .map__attribution {
      margin-top: var(--space-1);
      text-align: right;
    }
  `,
})
export class KioskMapComponent {
  private readonly theme = inject(ThemeService);
  private readonly engine = inject(MAP_ENGINE);
  private readonly settings = inject(MapConfigService);
  private readonly host = viewChild.required<ElementRef<HTMLDivElement>>('host');

  readonly markers = input<readonly MapMarker[]>([]);

  /** A picked point, shown as a single marker; set by the parent. */
  readonly picked = input<{ lat: number; lng: number } | null>(null);

  /** Whether a click picks a point (the kiosk form) rather than doing nothing. */
  readonly picking = input(false);

  readonly label = input('');

  readonly markerClick = output<string>();
  readonly pick = output<{ lat: number; lng: number }>();

  protected readonly state = signal<MapState>('loading');
  protected readonly attribution = signal<readonly MapAttributionLink[]>([]);

  private map?: MapHandle;
  private destroyed = false;

  constructor() {
    afterNextRender(() => void this.create());

    effect(() => {
      // Redrawn when the markers, the picked point or the theme change.
      const markers = this.markers();
      const picked = this.picked();
      this.theme.resolved();
      this.draw(markers, picked);
    });

    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.map?.remove();
    });
  }

  protected retry(): void {
    void this.create();
  }

  private async create(): Promise<void> {
    this.map?.remove();
    this.map = undefined;
    this.state.set('loading');

    try {
      const config = await this.settings.config();
      this.attribution.set(config.attribution);

      const map = await this.engine.create(this.host().nativeElement, {
        styleUrl: config.styleUrl,
        center: defaultCenter,
        zoom: 11,
      });

      if (this.destroyed) {
        map.remove();
        return;
      }

      this.map = map;
      map.onClick((point) => {
        if (this.picking()) {
          this.pick.emit({ lat: Number(point.lat.toFixed(6)), lng: Number(point.lng.toFixed(6)) });
        }
      });

      await withTimeout(map.ready, mapLoadTimeoutMs);
      if (this.destroyed) {
        return;
      }

      this.state.set('ready');
      this.draw(this.markers(), this.picked());
      this.fit();
    } catch {
      // The map server, or the settings, did not answer: the notice, not an empty box.
      this.map?.remove();
      this.map = undefined;

      if (!this.destroyed) {
        this.state.set('failed');
      }
    }
  }

  private draw(markers: readonly MapMarker[], picked: { lat: number; lng: number } | null): void {
    if (!this.map || this.state() !== 'ready') {
      return;
    }

    const drawn: EngineMarker[] = markers.map((marker) => {
      const element = this.dot(toneTokens[marker.tone]);
      element.setAttribute('data-marker', marker.id);
      element.setAttribute('role', 'button');
      element.setAttribute('aria-label', marker.title);
      element.addEventListener('click', () => this.markerClick.emit(marker.id));

      // Text content, not an HTML string: names come from administrators.
      const popup = document.createElement('div');
      const title = document.createElement('strong');
      title.textContent = marker.title;
      popup.append(title);

      if (marker.subtitle) {
        const subtitle = document.createElement('div');
        subtitle.textContent = marker.subtitle;
        popup.append(subtitle);
      }

      return { lat: marker.lat, lng: marker.lng, element, popup };
    });

    if (picked) {
      const element = this.dot('--brand-500');
      element.setAttribute('data-picked', '');
      drawn.push({ lat: picked.lat, lng: picked.lng, element });
    }

    this.map.setMarkers(drawn);
  }

  /** Frames every marker, or the picked point, once the map exists. */
  private fit(): void {
    const points: MapPoint[] = this.markers().map((marker) => ({ lat: marker.lat, lng: marker.lng }));
    const picked = this.picked();

    if (picked) {
      points.push(picked);
    }

    this.map?.fit(points);
  }

  /** A marker: a circle filled with a design token's colour, so the theme applies. */
  private dot(token: string): HTMLElement {
    const element = document.createElement('div');
    element.className = 'neo-map-marker';
    element.style.background = `var(${token})`;
    return element;
  }
}

function withTimeout<T>(promise: Promise<T>, ms: number): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('map timeout')), ms);
    promise.then(
      (value) => {
        clearTimeout(timer);
        resolve(value);
      },
      (error: unknown) => {
        clearTimeout(timer);
        reject(error);
      },
    );
  });
}
