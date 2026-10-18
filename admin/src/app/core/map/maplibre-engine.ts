import { LngLatBounds, Map as MapLibreMap, Marker, Popup, setWorkerUrl } from 'maplibre-gl';

import { EngineMarker, MapHandle, MapPoint } from './map-engine';

/**
 * The worker MapLibre draws in, served by the panel itself (angular.json
 * copies it to /maplibre/): no CDN, and the page's policy allows it.
 */
setWorkerUrl('/maplibre/maplibre-gl-worker.mjs');

/** MapLibre GL JS behind the panel's {@link MapHandle} (T-217). */
export function createMapLibreMap(container: HTMLElement, options: { styleUrl: string; center: MapPoint; zoom: number }): MapHandle {
  const map = new MapLibreMap({
    container,
    style: options.styleUrl,
    center: [options.center.lng, options.center.lat],
    zoom: options.zoom,
    // The attribution is the panel's own line under the map, always shown -
    // MapLibre's folds into an "i" on a narrow screen.
    attributionControl: false,
    dragRotate: false,
    pitchWithRotate: false,
  });
  map.touchZoomRotate.disableRotation();

  let drawn: Marker[] = [];
  let loaded = false;

  const ready = new Promise<void>((resolve, reject) => {
    map.once('load', () => {
      loaded = true;
      resolve();
    });

    // Before the style is in, an error is the map failing; after, it is one
    // tile, which the map lives without. Listening also keeps MapLibre from
    // writing it to the console itself.
    map.on('error', (event) => {
      if (!loaded) {
        reject(event.error ?? new Error('map error'));
      }
    });
  });

  return {
    ready,

    setMarkers(markers: readonly EngineMarker[]): void {
      drawn.forEach((marker) => marker.remove());
      drawn = markers.map((marker) => {
        const added = new Marker({ element: marker.element }).setLngLat([marker.lng, marker.lat]);

        if (marker.popup) {
          added.setPopup(new Popup({ offset: 14, closeButton: false }).setDOMContent(marker.popup));
        }

        return added.addTo(map);
      });
    },

    fit(points: readonly MapPoint[]): void {
      if (points.length === 1) {
        map.jumpTo({ center: [points[0].lng, points[0].lat], zoom: 15 });
      } else if (points.length > 1) {
        const bounds = new LngLatBounds();
        points.forEach((point) => bounds.extend([point.lng, point.lat]));
        map.fitBounds(bounds, { padding: 32, animate: false, maxZoom: 15 });
      }
    },

    onClick(listener: (point: MapPoint) => void): void {
      map.on('click', (event) => listener({ lat: event.lngLat.lat, lng: event.lngLat.lng }));
    },

    remove(): void {
      map.remove();
    },
  };
}
