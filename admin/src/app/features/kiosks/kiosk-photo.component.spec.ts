import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideTranslateService, TranslateService } from '@ngx-translate/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { translations } from '../../core/i18n/translations';
import { KioskPhotoComponent } from './kiosk-photo.component';

/** The kiosk's picture on its General tab (T-340, T-363). */
describe('KioskPhotoComponent', () => {
  let made = 0;
  const revoked: string[] = [];

  beforeEach(() => {
    made = 0;
    revoked.length = 0;
    // jsdom has no object URLs of its own.
    URL.createObjectURL = vi.fn(() => `blob:photo-${++made}`);
    URL.revokeObjectURL = vi.fn((url: string) => void revoked.push(url));

    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideTranslateService({ fallbackLang: 'az', lang: 'az' })],
    });
    const translate = TestBed.inject(TranslateService);
    translate.setTranslation('az', translations.az);
    translate.use('az');
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('fetches the picture once and keeps showing it - showing it does not fetch it again (T-363)', async () => {
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(KioskPhotoComponent);
    fixture.componentRef.setInput('kioskId', 'k-1');
    fixture.componentRef.setInput('photoUrl', 'api/admin/kiosks/k-1/photo?v=1');
    fixture.detectChanges();

    http.expectOne('/api/admin/kiosks/k-1/photo?v=1').flush(new Blob(['jpeg']));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    // Nothing more is asked for, and the picture shown is not taken away.
    http.expectNone(() => true);
    const image = (fixture.nativeElement as HTMLElement).querySelector('img');
    expect(image?.getAttribute('src')).toBe('blob:photo-1');
    expect(revoked).toEqual([]);
  });

  it('shows a new picture when the address changes and lets the old one go', async () => {
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(KioskPhotoComponent);
    fixture.componentRef.setInput('kioskId', 'k-1');
    fixture.componentRef.setInput('photoUrl', 'api/admin/kiosks/k-1/photo?v=1');
    fixture.detectChanges();
    http.expectOne('/api/admin/kiosks/k-1/photo?v=1').flush(new Blob(['old']));
    fixture.detectChanges();

    fixture.componentRef.setInput('photoUrl', 'api/admin/kiosks/k-1/photo?v=2');
    fixture.detectChanges();
    http.expectOne('/api/admin/kiosks/k-1/photo?v=2').flush(new Blob(['new']));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    http.expectNone(() => true);
    expect((fixture.nativeElement as HTMLElement).querySelector('img')?.getAttribute('src')).toBe('blob:photo-2');
    expect(revoked).toEqual(['blob:photo-1']);
  });

  it('shows the placeholder without a picture', () => {
    const fixture = TestBed.createComponent(KioskPhotoComponent);
    fixture.componentRef.setInput('kioskId', 'k-1');
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('img')).toBeNull();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('storefront');
  });
});
