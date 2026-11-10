import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideTranslateService, TranslateService } from '@ngx-translate/core';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { KioskBrandingView } from '../../core/api/admin-api.service';
import { translations } from '../../core/i18n/translations';
import { KioskBrandingComponent } from './kiosk-branding.component';

function view(partial: Partial<KioskBrandingView> = {}): KioskBrandingView {
  return {
    kioskId: 'k-1',
    texts: {},
    defaults: {},
    image: null,
    defaultImage: null,
    keys: [],
    maxLength: 160,
    updatedAt: null,
    ...partial,
  };
}

/** The welcome screen's words for every kiosk and for one (T-387). */
describe('KioskBrandingComponent', () => {
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
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it("shows a kiosk's own words, the default's where it has none, and saves every text", async () => {
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(KioskBrandingComponent);
    fixture.componentRef.setInput('kioskId', 'k-1');
    fixture.componentRef.setInput('canEdit', true);
    fixture.detectChanges();

    http.expectOne('/api/admin/kiosks/k-1/branding').flush(
      view({
        texts: { heroTitle: { az: 'Metroda çap' } },
        defaults: { heroTitle: { en: 'Everything prints' } },
      }),
    );
    await fixture.whenStable();
    fixture.detectChanges();

    const component = fixture.componentInstance;
    expect(component.texts()['heroTitle']).toEqual({ az: 'Metroda çap', en: '', ru: '' });
    expect(component.placeholder('heroTitle', 'en')).toBe('Everything prints');
    expect(component.placeholder('usbTitle', 'az')).toBe('kioskBranding.builtIn');

    component.edit('hotline', 'az', 'Kömək: *1234');
    const saving = component.save();
    const put = http.expectOne('/api/admin/kiosks/k-1/branding');
    expect(put.request.method).toBe('PUT');
    expect(put.request.body.values.heroTitle.az).toBe('Metroda çap');
    expect(put.request.body.values.hotline.az).toBe('Kömək: *1234');
    put.flush(
      view({ texts: { heroTitle: { az: 'Metroda çap' }, hotline: { az: 'Kömək: *1234' } } }),
    );
    await saving;

    expect(component.dirty()).toBe(false);
  });

  it('works on the default for every kiosk without a kiosk id', async () => {
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(KioskBrandingComponent);
    fixture.componentRef.setInput('canEdit', true);
    fixture.detectChanges();

    http.expectOne('/api/admin/kiosk-branding').flush(view({ kioskId: null, defaults: null }));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.componentInstance.placeholder('heroTitle', 'az')).toBe('kioskBranding.builtIn');
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Kiosk ekranı');
  });
});
