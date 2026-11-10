import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { AuthService } from '../../core/auth/auth.service';
import { OperatorScanComponent } from './operator-scan.component';

/** "Kioskun QR-ını oxut" (T-425): one button; the code says which page it is for. */
describe('OperatorScanComponent', () => {
  const allowed = signal(true);

  beforeEach(() => {
    allowed.set(true);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
        { provide: AuthService, useValue: { can: () => allowed() } },
      ],
    });
  });

  it('sends a login QR to "Kioska daxil ol" and an activation QR to "Kiosku aktivləşdir", with the code', async () => {
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
    const scan = TestBed.createComponent(OperatorScanComponent).componentInstance;

    await scan.open('neoprint://kiosk-login/0a1b');
    expect(navigate).toHaveBeenLastCalledWith('/operator/kiosk-login', {
      state: { code: 'neoprint://kiosk-login/0a1b' },
    });

    await scan.open('neoprint://activate/x?code=AB12');
    expect(navigate).toHaveBeenLastCalledWith('/operator/activate', {
      state: { code: 'neoprint://activate/x?code=AB12' },
    });
  });

  it('reads no activation QR for an account that may not activate, and no other code at all', () => {
    const scan = TestBed.createComponent(OperatorScanComponent).componentInstance;

    expect(scan.routeFor('https://example.org')).toBeNull();

    allowed.set(false);
    expect(scan.routeFor('neoprint://activate/x?code=AB12')).toBeNull();
    expect(scan.routeFor('neoprint://kiosk-login/0a1b')).toBe('/operator/kiosk-login');
  });
});
