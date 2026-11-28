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

/** A dialog closes after its animation. */
const closed = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 400));

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
      cards: ['phone', 'usb'],
      features: ['featureFast'],
      imageLeft: true,
    };
    http.expectOne('/api/admin/kiosks/k-1/branding').flush(view({ defaultLayout: fallback }));
    await fixture.whenStable();
    fixture.detectChanges();

    const component = fixture.componentInstance;
    expect(component.layout()).toEqual(fallback);
    expect(component.atDefaultLayout()).toBe(true);

    const own = { ...fallback, cards: ['usb', 'phone'] };
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

  it('edits a part in place: its pencil opens its texts, the preview follows, "Ləğv et" puts them back (T-494)', async () => {
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(KioskBrandingComponent);
    fixture.componentRef.setInput('canEdit', true);
    fixture.detectChanges();

    http.expectOne('/api/admin/kiosk-branding').flush(view({ kioskId: null, defaults: null }));
    await fixture.whenStable();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    // No text boxes under the preview any more - the pencils on it instead.
    expect(host.querySelector('input[matInput]')).toBeNull();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
    const pens = host.querySelectorAll<HTMLButtonElement>('[data-pen]');
    // The big line, the picture, two cards, the help line and four features.
    expect(pens.length).toBe(9);
    expect(pens[0].getAttribute('aria-label')).toBe('Redaktə et: Ana ekran');

    const component = fixture.componentInstance;
    const before = component.texts()['heroTitle']['az'];

    pens[0].click();
    await fixture.whenStable();
    fixture.detectChanges();

    const field = document.querySelector<HTMLInputElement>('[data-field="heroTitle-az"]');
    expect(field).not.toBeNull();
    expect(document.querySelector('[data-field="heroSubtitle-ru"]')).not.toBeNull();
    expect(document.querySelector('[data-field="usbTitle-az"]')).toBeNull();

    field!.value = 'Metroda çap';
    field!.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(component.texts()['heroTitle']['az']).toBe('Metroda çap');
    expect(host.querySelector('.hero__title')?.textContent).toContain('Metroda çap');
    expect(host.querySelector('[data-unsaved]')).not.toBeNull();

    document.querySelector<HTMLButtonElement>('[data-cancel]')!.click();
    await closed();
    fixture.detectChanges();
    expect(component.texts()['heroTitle']['az']).toBe(before);
    expect(component.dirty()).toBe(false);

    // "Hazır" keeps them, to be saved.
    component.openTexts({ titleKey: 'kioskBranding.groupHotline', keys: ['hotline'] });
    await fixture.whenStable();
    fixture.detectChanges();
    const hotline = document.querySelector<HTMLInputElement>('[data-field="hotline-az"]')!;
    hotline.value = '*1234';
    hotline.dispatchEvent(new Event('input'));
    document.querySelector<HTMLButtonElement>('[data-done]')!.click();
    await closed();
    fixture.detectChanges();
    expect(component.texts()['hotline']['az']).toBe('*1234');
    expect(component.dirty()).toBe(true);
  });

  it('shows no pencils to an account that may not edit (T-494)', async () => {
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(KioskBrandingComponent);
    fixture.componentRef.setInput('kioskId', 'k-1');
    fixture.detectChanges();

    http.expectOne('/api/admin/kiosks/k-1/branding').flush(view());
    await fixture.whenStable();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('[data-pen]')).toBeNull();
  });
});
