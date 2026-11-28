import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideTranslateService, TranslateService } from '@ngx-translate/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { CameraFootage } from '../../core/api/camera-api.service';
import { translations } from '../../core/i18n/translations';
import { KioskCameraComponent } from './kiosk-camera.component';

const base = '/api/admin/kiosks/k-1/camera';
const tick = (ms = 0): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

function footage(partial: Partial<CameraFootage> = {}): CameraFootage {
  return { cameraOn: true, minutes: [], request: null, ...partial };
}

function minute(id: string, start: string) {
  return { id, start, end: start, bytes: 1000, fetched: false, expiresAt: start };
}

/** Kiosk detail → "Kamera" (T-483). */
describe('KioskCameraComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      ],
    });
    const translate = TestBed.inject(TranslateService);
    translate.setTranslation('az', translations.az);
    translate.use('az');

    let next = 0;
    URL.createObjectURL = vi.fn(() => `blob:test-${next++}`);
    URL.revokeObjectURL = vi.fn();
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  async function create(first: CameraFootage) {
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(KioskCameraComponent);
    fixture.componentRef.setInput('kioskId', 'k-1');
    fixture.componentRef.setInput('kioskCode', 'K-1042');
    fixture.detectChanges();

    http.expectOne((r) => r.url === `${base}/footage`).flush(first);
    await fixture.whenStable();
    await tick();
    fixture.detectChanges();

    return { http, fixture, host: fixture.nativeElement as HTMLElement };
  }

  it('says the camera is off when it is, and offers nothing to watch', async () => {
    const { host } = await create(footage({ cameraOn: false }));

    expect(host.textContent).toContain('Bu kioskda kamera söndürülüb');
    expect(host.querySelector('[data-watch]')).toBeNull();
  });

  it('watches live: asks the kiosk, shows each frame, and stops - on the button and on leaving (T-483)', async () => {
    const { http, fixture, host } = await create(footage());

    host.querySelector<HTMLButtonElement>('[data-watch]')!.click();
    const start = http.expectOne(`${base}/live`);
    expect(start.request.method).toBe('POST');
    start.flush({ live: true });
    await tick(20);
    fixture.detectChanges();

    http.expectOne(`${base}/live/frame`).flush(new Blob([new Uint8Array([0xff, 0xd8])]), {
      headers: { 'X-Frame-At': '2026-11-28T16:42:05Z' },
    });
    await tick();
    fixture.detectChanges();

    expect(host.querySelector('[data-live] img')?.getAttribute('src')).toBe('blob:test-0');
    expect(host.textContent).toContain('Canlı');
    expect(host.querySelector('[data-silent]')).toBeNull();

    host.querySelector<HTMLButtonElement>('[data-stop]')!.click();
    expect(http.expectOne(`${base}/live`).request.method).toBe('DELETE');
    fixture.detectChanges();
    expect(host.querySelector('[data-live] img')).toBeNull();

    // Watching again, then leaving the tab: the watch ends too.
    fixture.componentInstance.startLive();
    http.expectOne(`${base}/live`).flush({ live: true });
    await tick(20);
    http.expectOne(`${base}/live/frame`).flush(null, { status: 204, statusText: 'No Content' });
    fixture.destroy();
    expect(http.expectOne(`${base}/live`).request.method).toBe('DELETE');
  });

  it('plays the minutes on the server and offers to fetch the rest from the kiosk (T-483)', async () => {
    const { http, fixture, host } = await create(footage());

    host.querySelector<HTMLButtonElement>('[data-show]')!.click();
    http
      .expectOne((r) => r.url === `${base}/footage`)
      .flush(
        footage({
          minutes: [minute('m1', '2026-11-28T16:40:00Z'), minute('m2', '2026-11-28T16:41:00Z')],
        }),
      );
    await tick();
    http.expectOne(`${base}/footage/m1`).flush(new Blob([new Uint8Array([1, 2])]));
    await tick();
    fixture.detectChanges();

    expect(host.querySelector('video')?.getAttribute('src')).toBe('blob:test-0');
    expect(host.querySelectorAll('[data-minute]').length).toBe(2);
    expect(host.querySelector('[data-missing]')?.textContent).toContain('2 dəqiqəsi serverdədir');
    expect(host.querySelector('a[download]')?.getAttribute('download')).toMatch(
      /^K-1042-\d{8}-\d{4}\.mp4$/,
    );

    // The minute ends: the next plays.
    const playing = fixture.componentInstance.next();
    http.expectOne(`${base}/footage/m2`).flush(new Blob([new Uint8Array([3])]));
    await playing;

    host.querySelector<HTMLButtonElement>('[data-fetch-button]')!.click();
    const asked = http.expectOne(`${base}/footage/requests`);
    expect(asked.request.method).toBe('POST');
    asked.flush({
      id: 'r1',
      from: '2026-11-28T16:40:00Z',
      to: '2026-11-28T16:55:00Z',
      status: 'Pending',
      delivered: 0,
      createdAt: new Date().toISOString(),
      deliveredAt: null,
    });
    await tick();
    fixture.detectChanges();

    expect(host.querySelector('[data-fetch="sending"]')?.textContent).toContain(
      'Kiosk dəqiqələri göndərir',
    );
    expect(host.querySelector('[data-missing]')).toBeNull();
    fixture.destroy();
  });

  it('says so when the kiosk had nothing of the span', async () => {
    const { http, fixture, host } = await create(footage());

    host.querySelector<HTMLButtonElement>('[data-show]')!.click();
    http
      .expectOne((r) => r.url === `${base}/footage`)
      .flush(
        footage({
          request: {
            id: 'r1',
            from: '2026-11-28T16:40:00Z',
            to: '2026-11-28T16:55:00Z',
            status: 'Delivered',
            delivered: 0,
            createdAt: '2026-11-28T16:56:00Z',
            deliveredAt: '2026-11-28T16:56:10Z',
          },
        }),
      );
    await tick();
    fixture.detectChanges();

    expect(host.querySelector('[data-fetch="nothing"]')?.textContent).toContain(
      'Kiosk bu aralıqda yazı tapmadı',
    );
    expect(host.querySelector('[data-fetch-button]')).toBeNull();
  });
});
