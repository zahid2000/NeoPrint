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
  viewChild,
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import * as L from 'leaflet';

import { ThemeService } from '../core/theme/theme.service';
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
const defaultCenter: L.LatLngTuple = [40.4093, 49.8671];

const toneTokens: Record<StatusTone, string> = {
  success: '--success-500',
  warning: '--warning-500',
  danger: '--danger-500',
  info: '--info-500',
  neutral: '--neutral-400',
};

/**
 * Kiosks on an OpenStreetMap map (Leaflet), or a point picker for the kiosk
 * form (docs/DESIGN_SYSTEM.md section 4, "Xəritə").
 *
 * Markers are circles in the colour of their status tone, read from the
 * design tokens so the theme applies. Colour is never the only signal: the
 * page shows a legend with icons and words, and a marker's popup names the
 * kiosk and its status.
 */
@Component({
  selector: 'app-kiosk-map',
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div #host class="map" role="application" [attr.aria-label]="label()"></div>
    <p class="neo-caption map__attribution">{{ 'common.mapAttribution' | translate }}</p>
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
    }

    .map__attribution {
      margin-top: var(--space-1);
      text-align: right;
    }
  `,
})
export class KioskMapComponent {
  private readonly theme = inject(ThemeService);
  private readonly host = viewChild.required<ElementRef<HTMLDivElement>>('host');

  readonly markers = input<readonly MapMarker[]>([]);

  /** A picked point, shown as a single marker; set by the parent. */
  readonly picked = input<{ lat: number; lng: number } | null>(null);

  /** Whether a click picks a point (the kiosk form) rather than doing nothing. */
  readonly picking = input(false);

  readonly label = input('');

  readonly markerClick = output<string>();
  readonly pick = output<{ lat: number; lng: number }>();

  private map?: L.Map;
  private layer?: L.LayerGroup;

  constructor() {
    afterNextRender(() => this.create());

    effect(() => {
      // Redrawn when the markers, the picked point or the theme change.
      const markers = this.markers();
      const picked = this.picked();
      this.theme.resolved();
      this.draw(markers, picked);
    });

    inject(DestroyRef).onDestroy(() => this.map?.remove());
  }

  private create(): void {
    this.map = L.map(this.host().nativeElement, {
      center: defaultCenter,
      zoom: 11,
      attributionControl: false,
    });

    // The standard OpenStreetMap tiles; the attribution is shown under the
    // map, as their usage policy requires.
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 19 }).addTo(this.map);

    this.layer = L.layerGroup().addTo(this.map);

    this.map.on('click', (event: L.LeafletMouseEvent) => {
      if (this.picking()) {
        this.pick.emit({
          lat: Number(event.latlng.lat.toFixed(6)),
          lng: Number(event.latlng.lng.toFixed(6)),
        });
      }
    });

    this.draw(this.markers(), this.picked());
    this.fit();
  }

  private draw(markers: readonly MapMarker[], picked: { lat: number; lng: number } | null): void {
    if (!this.map || !this.layer) {
      return;
    }

    this.layer.clearLayers();

    for (const marker of markers) {
      const circle = L.circleMarker([marker.lat, marker.lng], {
        radius: 9,
        weight: 2,
        color: this.token('--neutral-0'),
        fillColor: this.token(toneTokens[marker.tone]),
        fillOpacity: 1,
      });

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

      circle.bindPopup(popup);
      circle.on('click', () => this.markerClick.emit(marker.id));
      circle.addTo(this.layer);
    }

    if (picked) {
      L.circleMarker([picked.lat, picked.lng], {
        radius: 10,
        weight: 3,
        color: this.token('--neutral-0'),
        fillColor: this.token('--brand-500'),
        fillOpacity: 1,
      }).addTo(this.layer);
    }
  }

  /** Frames every marker, or the picked point, once the map exists. */
  private fit(): void {
    if (!this.map) {
      return;
    }

    const points: L.LatLngTuple[] = this.markers().map((marker) => [marker.lat, marker.lng]);
    const picked = this.picked();

    if (picked) {
      points.push([picked.lat, picked.lng]);
    }

    if (points.length === 1) {
      this.map.setView(points[0], 15);
    } else if (points.length > 1) {
      this.map.fitBounds(L.latLngBounds(points), { padding: [32, 32] });
    }
  }

  private token(name: string): string {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  }
}
