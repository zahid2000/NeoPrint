import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MatDialogRef } from '@angular/material/dialog';
import { provideTranslateService } from '@ngx-translate/core';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { EnrollmentTokenDialogComponent, maxTokenUses, tokenLifetimes } from './enrollment-token.dialog';

/** The enrollment token dialog (T-327, docs/DESIGN_SYSTEM.md "Cihazlar → Qeydiyyat açarları"). */
describe('EnrollmentTokenDialogComponent', () => {
  let backend: HttpTestingController;
  let closed: unknown[];

  beforeEach(() => {
    closed = [];
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
        { provide: MatDialogRef, useValue: { close: (value: unknown) => closed.push(value) } },
      ],
    });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  async function open() {
    const fixture = TestBed.createComponent(EnrollmentTokenDialogComponent);
    fixture.detectChanges();
    backend.expectOne('/api/admin/kiosks').flush([
      { id: 'k1', code: 'KSK-9007', name: 'Free', activeDeviceId: null },
      { id: 'k2', code: 'KSK-9002', name: 'Taken', activeDeviceId: 'd' },
    ]);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  it('offers the lifetimes as a list, a day by default, and the number of new kiosks up to the limit', async () => {
    const fixture = await open();
    const dialog = fixture.componentInstance;

    expect(tokenLifetimes).toEqual([1, 24, 72, 168, 720]);
    expect(dialog.form.getRawValue().ttlHours).toBe(24);
    expect(fixture.nativeElement.querySelector('[data-field="lifetime"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-field="uses"]')).not.toBeNull();

    dialog.newKioskForm.patchValue({ name: 'Pilot', address: 'Bakı', adminPin: '135790', uses: maxTokenUses + 1 });
    expect(dialog.invalid, 'more kiosks than one token may make').toBe(true);
    dialog.newKioskForm.patchValue({ uses: 3 });
    expect(dialog.invalid).toBe(false);
  });

  it('sends a token for new kiosks with their template and count, then shows it once with what it can do', async () => {
    const fixture = await open();
    const dialog = fixture.componentInstance;
    dialog.newKioskForm.patchValue({ name: 'Pilot', address: 'Bakı, 28 May', adminPin: '135790', uses: 3 });
    dialog.form.patchValue({ ttlHours: 72 });

    const submitted = dialog.submit();
    const request = backend.expectOne('/api/admin/enrollment-tokens');
    expect(request.request.body).toMatchObject({
      kioskId: null,
      ttlHours: 72,
      maxUses: 3,
      newKiosk: { name: 'Pilot', address: 'Bakı, 28 May', adminPin: '135790', code: null },
    });
    request.flush({
      id: 't', token: 'abc123', expiresAt: new Date().toISOString(), kioskId: null, kioskCode: null,
      windowsCommand: 'provision.ps1 -EnrollmentToken abc123', linuxCommand: 'sudo ./provision.sh --enrollment-token abc123', maxUses: 3,
    });
    await submitted;
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-token]').textContent).toBe('abc123');
    expect(fixture.nativeElement.querySelector('[data-once-only]'), 'said it will not be shown again').not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-token-reach]').textContent).toContain('tokens.reach');
    expect(fixture.nativeElement.querySelector('[data-submit]'), 'it cannot be made again from here').toBeNull();
  });

  it('binds a token to a kiosk that has no device yet, once', async () => {
    const fixture = await open();
    const dialog = fixture.componentInstance;

    expect(dialog.bindableKiosks().map((kiosk) => kiosk.code), 'a kiosk with its device is not offered').toEqual(['KSK-9007']);
    dialog.form.patchValue({ mode: 'existing' });
    expect(dialog.invalid, 'which kiosk?').toBe(true);
    dialog.form.patchValue({ kioskId: 'k1' });
    expect(dialog.invalid).toBe(false);

    const submitted = dialog.submit();
    const request = backend.expectOne('/api/admin/enrollment-tokens');
    expect(request.request.body).toEqual({ kioskId: 'k1', newKiosk: null, ttlHours: 24, maxUses: 1 });
    request.flush('', { status: 409, statusText: 'Conflict' });
    await submitted;

    expect(dialog.errorMessage()).not.toBeNull();
    expect(dialog.created()).toBeNull();
  });
});
