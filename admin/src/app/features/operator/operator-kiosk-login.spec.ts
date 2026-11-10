import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { provideTranslateService, TranslateService } from '@ngx-translate/core';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { translations } from '../../core/i18n/translations';
import { OperatorKioskLoginComponent } from './operator-kiosk-login.component';
import { kioskLoginQr } from './qr-scanner';

/** "Kioska daxil ol" (T-392): the kiosk's QR opens its menu in this operator's name. */
describe('OperatorKioskLoginComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        provideRouter([]),
        provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      ],
    });
    const translate = TestBed.inject(TranslateService);
    translate.setTranslation('az', translations.az);
    translate.use('az');
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('reads only a kiosk login QR', () => {
    expect(kioskLoginQr('neoprint://kiosk-login/0a1b')).toBe(true);
    expect(kioskLoginQr('neoprint://activate/x?code=1')).toBe(false);
  });

  it('confirms the code and says which kiosk opened', async () => {
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(OperatorKioskLoginComponent);
    fixture.detectChanges();

    const approving = fixture.componentInstance.approve(' neoprint://kiosk-login/0a1b ');
    const request = http.expectOne('/api/admin/operator/kiosk-logins/approve');
    expect(request.request.body).toEqual({ code: 'neoprint://kiosk-login/0a1b' });
    request.flush({ kioskId: 'k-1', kioskCode: 'KSK-0042', kioskName: 'Metro' });
    await approving;
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('KSK-0042');
  });
});
