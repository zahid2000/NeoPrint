import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideTranslateService, TranslateService } from '@ngx-translate/core';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { EngineMarker, MAP_ENGINE, MapEngine, MapHandle, MapPoint } from '../core/map/map-engine';
import { translations } from '../core/i18n/translations';
import { KioskMapComponent, MapMarker, mapLoadTimeoutMs } from './kiosk-map.component';

/** A map that draws nothing and tells what it was asked to (T-217: jsdom has no WebGL). */
class FakeMap implements MapHandle {
  markers: readonly EngineMarker[] = [];
  fitted: readonly MapPoint[] = [];
  removed = false;
  private clickListener?: (point: MapPoint) => void;
  private settle!: { resolve: () => void; reject: (error: unknown) => void };

  readonly ready = new Promise<void>((resolve, reject) => (this.settle = { resolve, reject }));

  constructor(readonly styleUrl: string) {}

  load(): void {
    this.settle.resolve();
  }

  fail(): void {
    this.settle.reject(new Error('style 503'));
  }

  click(point: MapPoint): void {
    this.clickListener?.(point);
  }

  setMarkers(markers: readonly EngineMarker[]): void {
    this.markers = markers;
  }

  fit(points: readonly MapPoint[]): void {
    this.fitted = points;
  }

  onClick(listener: (point: MapPoint) => void): void {
    this.clickListener = listener;
  }

  remove(): void {
    this.removed = true;
  }
}

class FakeEngine implements MapEngine {
  readonly maps: FakeMap[] = [];

  async create(_container: HTMLElement, options: { styleUrl: string }): Promise<MapHandle> {
    const map = new FakeMap(options.styleUrl);
    this.maps.push(map);
    return map;
  }
}

@Component({
  imports: [KioskMapComponent],
  template: `<app-kiosk-map
    [markers]="markers()"
    [picked]="picked()"
    [picking]="picking()"
    label="Kiosklar"
    (markerClick)="clicked = $event"
    (pick)="pickedAt = $event"
  />`,
})
class Host {
  readonly markers = signal<readonly MapMarker[]>([]);
  readonly picked = signal<{ lat: number; lng: number } | null>(null);
  readonly picking = signal(false);
  clicked?: string;
  pickedAt?: { lat: number; lng: number };
}

const config = {
  styleUrl: 'https://tiles.openfreemap.org/styles/liberty',
  attribution: [
    { label: 'OpenFreeMap', url: 'https://openfreemap.org' },
    { label: '© OpenMapTiles', url: 'https://www.openmaptiles.org/' },
    { label: '© OpenStreetMap contributors', url: 'https://www.openstreetmap.org/copyright' },
  ],
};

let engine: FakeEngine;
let backend: HttpTestingController;

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 4; i++) {
    await Promise.resolve();
    await new Promise((resolve) => setTimeout(resolve));
  }
  fixture.detectChanges();
}

async function open(setup: (host: Host) => void = () => undefined): Promise<ComponentFixture<Host>> {
  engine = new FakeEngine();
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      { provide: MAP_ENGINE, useValue: engine },
    ],
  });
  TestBed.inject(TranslateService).setTranslation('az', translations.az);
  TestBed.inject(TranslateService).use('az');
  backend = TestBed.inject(HttpTestingController);

  const fixture = TestBed.createComponent(Host);
  setup(fixture.componentInstance);
  fixture.detectChanges();
  await settle(fixture);
  return fixture;
}

const element = (fixture: ComponentFixture<unknown>) => fixture.nativeElement as HTMLElement;

afterEach(() => {
  vi.useRealTimers();
  backend.verify();
});

describe('Kiosk xəritəsi (OpenFreeMap)', () => {
  it('draws the style the server names, credits all three as links, and puts the kiosks on it', async () => {
    const fixture = await open((host) =>
      host.markers.set([
        { id: 'k-1', lat: 40.4, lng: 49.8, title: 'Mərkəz', subtitle: 'KSK-0001', tone: 'success' },
        { id: 'k-2', lat: 40.5, lng: 49.9, title: '<b>Nərimanov</b>', tone: 'danger' },
      ]),
    );
    backend.expectOne('/api/map/config').flush(config);
    await settle(fixture);

    const map = engine.maps[0];
    expect(map.styleUrl).toBe('https://tiles.openfreemap.org/styles/liberty');

    // The attribution is there while the map loads, every part a link to its owner.
    const links = [...element(fixture).querySelectorAll<HTMLAnchorElement>('[data-map-attribution] a')];
    expect(links.map((link) => link.textContent)).toEqual(['OpenFreeMap', '© OpenMapTiles', '© OpenStreetMap contributors']);
    expect(links.map((link) => link.getAttribute('href'))).toEqual(config.attribution.map((item) => item.url));
    expect(links.every((link) => link.rel.includes('noopener') && link.target === '_blank')).toBe(true);
    expect(element(fixture).querySelector('.map')!.getAttribute('aria-busy')).toBe('true');

    map.load();
    await settle(fixture);

    expect(map.markers).toHaveLength(2);
    expect(map.markers[0].element.style.background).toBe('var(--success-500)');
    // A name from an administrator is text, never markup.
    expect(map.markers[1].popup!.querySelector('b')).toBeNull();
    expect(map.markers[1].popup!.textContent).toContain('<b>Nərimanov</b>');
    expect(map.fitted).toHaveLength(2);

    map.markers[0].element.click();
    expect(fixture.componentInstance.clicked).toBe('k-1');
    expect(element(fixture).querySelector('[data-map-attribution]')).not.toBeNull();
  });

  it('picks a point in the kiosk form, rounded, and shows it', async () => {
    const fixture = await open((host) => host.picking.set(true));
    backend.expectOne('/api/map/config').flush(config);
    await settle(fixture);
    engine.maps[0].load();
    await settle(fixture);

    engine.maps[0].click({ lat: 40.123456789, lng: 49.987654321 });
    expect(fixture.componentInstance.pickedAt).toEqual({ lat: 40.123457, lng: 49.987654 });

    fixture.componentInstance.picked.set({ lat: 40.123457, lng: 49.987654 });
    fixture.detectChanges();
    expect(engine.maps[0].markers.at(-1)!.element.hasAttribute('data-picked')).toBe(true);
  });

  it('gives way to a notice with a retry when the map server does not answer in time, and tries again', async () => {
    const fixture = await open();
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    backend.expectOne('/api/map/config').flush(config);
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    // The style never comes.
    await vi.advanceTimersByTimeAsync(mapLoadTimeoutMs + 10);
    fixture.detectChanges();

    const notice = element(fixture).querySelector('.map__failed')!;
    expect(notice.textContent).toContain('Xəritə yüklənmədi');
    expect(notice.textContent).toContain('Kiosklar siyahıda görünür');
    expect(element(fixture).querySelector('.map')!.classList).toContain('map--hidden');
    expect(engine.maps[0].removed).toBe(true);

    // Retry: a new map, which loads this time.
    (notice.querySelector('button') as HTMLButtonElement).click();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
    engine.maps[1].load();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    expect(element(fixture).querySelector('.map__failed')).toBeNull();
    expect(element(fixture).querySelector('[data-map-attribution]')).not.toBeNull();
  });

  it('says so at once when the style is refused, and in the kiosk form asks for the coordinates by hand', async () => {
    const fixture = await open((host) => host.picking.set(true));
    backend.expectOne('/api/map/config').flush(config);
    await settle(fixture);
    engine.maps[0].fail();
    await settle(fixture);

    expect(element(fixture).querySelector('.map__failed')!.textContent).toContain('Koordinatları yuxarıdakı sahələrə əl ilə yazın');
  });

  it('shows the notice when even the settings cannot be read, and asks for them again on retry', async () => {
    const fixture = await open();
    backend.expectOne('/api/map/config').flush(null, { status: 503, statusText: 'Unavailable' });
    await settle(fixture);
    expect(element(fixture).querySelector('.map__failed')).not.toBeNull();
    expect(engine.maps).toHaveLength(0);

    (element(fixture).querySelector('.map__failed button') as HTMLButtonElement).click();
    await settle(fixture);
    backend.expectOne('/api/map/config').flush(config);
    await settle(fixture);
    expect(engine.maps).toHaveLength(1);
  });
});
