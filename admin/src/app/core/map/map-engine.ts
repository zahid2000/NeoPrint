import { HttpClient } from '@angular/common/http';
import { Injectable, InjectionToken, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** One link of the attribution line under every map. */
export interface MapAttributionLink {
  readonly label: string;
  readonly url: string;
}

/** `GET api/map/config` (T-217): the style to draw and who to credit, from the server's settings. */
export interface MapConfig {
  readonly styleUrl: string;
  readonly attribution: readonly MapAttributionLink[];
}

/** A point on the map, by longitude and latitude. */
export interface MapPoint {
  readonly lat: number;
  readonly lng: number;
}

/** A marker as the engine draws it: an element of the page's own, at a point. */
export interface EngineMarker extends MapPoint {
  readonly element: HTMLElement;

  /** Shown on click; built from text, never from an HTML string. */
  readonly popup?: HTMLElement;
}

/** A drawn map. Everything the kiosk map does with one, and nothing more. */
export interface MapHandle {
  /** Settles when the style has loaded; rejects when it fails before that. */
  readonly ready: Promise<void>;

  setMarkers(markers: readonly EngineMarker[]): void;

  /** Frames the points: one is centred, several are fitted. */
  fit(points: readonly MapPoint[]): void;

  onClick(listener: (point: MapPoint) => void): void;

  remove(): void;
}

/**
 * Draws maps (T-217). The panel's one is MapLibre GL JS; tests give a fake one,
 * since jsdom has no WebGL. Behind a token so the kiosk map is tested on what
 * it does - markers, attribution, the fallback - rather than on pixels.
 */
export interface MapEngine {
  create(container: HTMLElement, options: { styleUrl: string; center: MapPoint; zoom: number }): Promise<MapHandle>;
}

export const MAP_ENGINE = new InjectionToken<MapEngine>('MapEngine', {
  providedIn: 'root',
  // Loaded with the first map only: MapLibre is most of a megabyte.
  factory: () => ({
    create: async (container, options) => (await import('./maplibre-engine')).createMapLibreMap(container, options),
  }),
});

/**
 * The map's settings, asked once a session; a failed answer is not kept, so
 * the next map that opens asks again.
 */
@Injectable({ providedIn: 'root' })
export class MapConfigService {
  private readonly http = inject(HttpClient);
  private pending?: Promise<MapConfig>;

  config(): Promise<MapConfig> {
    this.pending ??= firstValueFrom(this.http.get<MapConfig>('/api/map/config')).catch((error: unknown) => {
      this.pending = undefined;
      throw error;
    });

    return this.pending;
  }
}
