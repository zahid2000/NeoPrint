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

  it("fills every field - its own words, the default page's, else the kiosk's - and keeps only what differs (T-422)", async () => {
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
    expect(component.texts()['heroTitle']).toEqual({
      az: 'Metroda çap',
      en: 'Everything prints',
      ru: 'Печатайте документы здесь',
    });
    expect(component.texts()['usbTitle']['az']).toBe('USB / Fleş kart');
    // The kiosk has no help line of its own.
    expect(component.texts()['hotline']['az']).toBe('');

    component.edit('hotline', 'az', 'Kömək: *1234');
    const saving = component.save();
    const put = http.expectOne('/api/admin/kiosks/k-1/branding');
    expect(put.request.method).toBe('PUT');
    expect(put.request.body.values.heroTitle.az).toBe('Metroda çap');
    // The same as the default: not kept here.
    expect(put.request.body.values.heroTitle.en).toBe('');
    expect(put.request.body.values.usbTitle.az).toBe('');
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

    expect(fixture.componentInstance.texts()['heroTitle']['az']).toBe(
      'Sənədlərinizi burada çap edin',
    );
    expect(fixture.componentInstance.atDefaults()).toBe(true);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Kiosk ekranı');
  });
  it('saves the arrangement dragged on the preview, and one the same as the default as none (T-441)', async () => {
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(KioskBrandingComponent);
    fixture.componentRef.setInput('kioskId', 'k-1');
    fixture.componentRef.setInput('canEdit', true);
    fixture.detectChanges();

    const fallback = {
      cards: ['code', 'usb', 'phone'],
      features: ['featureFast'],
      imageLeft: true,
    };
    http.expectOne('/api/admin/kiosks/k-1/branding').flush(view({ defaultLayout: fallback }));
    await fixture.whenStable();
    fixture.detectChanges();

    const component = fixture.componentInstance;
    expect(component.layout()).toEqual(fallback);
    expect(component.atDefaultLayout()).toBe(true);

    const own = { ...fallback, cards: ['phone', 'code', 'usb'] };
    component.arrange(own);
    expect(component.layoutDirty()).toBe(true);

    let saving = component.save();
    let put = http.expectOne('/api/admin/kiosks/k-1/branding/layout');
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ layout: own });
    put.flush(view({ layout: own, defaultLayout: fallback }));
    await saving;
    expect(component.layoutDirty()).toBe(false);
    expect(component.layout()).toEqual(own);

    // Back to the default's arrangement: the kiosk follows the default again.
    component.arrange(fallback);
    saving = component.save();
    put = http.expectOne('/api/admin/kiosks/k-1/branding/layout');
    expect(put.request.body).toEqual({ layout: null });
    put.flush(view({ layout: null, defaultLayout: fallback }));
    await saving;
  });
});
