import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { KioskEditorComponent } from './kiosk-editor.component';

describe('KioskEditorComponent (new kiosk)', () => {
  let fixture: ComponentFixture<KioskEditorComponent>;
  let component: KioskEditorComponent;
  let backend: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'kiosks/:id', children: [] }]),
        provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      ],
    });

    // The map needs a real browser; a form test does not.
    TestBed.overrideComponent(KioskEditorComponent, {
      set: { template: '<div></div>' },
    });

    backend = TestBed.inject(HttpTestingController);

    fixture = TestBed.createComponent(KioskEditorComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
  });

  afterEach(() => backend.verify());

  function fillIn(): void {
    component.name.set('Nizami küçəsi');
    component.address.set('Nizami küç. 10, Bakı');
    component.onPick({ lat: 40.4093, lng: 49.8671 });
    component.pin.set('482915');
    component.pinConfirm.set('482915');
  }

  it('asks for a name, an address and a position', () => {
    expect(component.isNew()).toBe(true);
    expect(component.generalProblems()).toEqual([
      'kiosks.invalid.name',
      'kiosks.invalid.address',
      'kiosks.invalid.location',
    ]);
    expect(component.canCreate()).toBe(false);
  });

  it('checks the PIN the way the server does', () => {
    fillIn();

    component.pin.set('12ab');
    expect(component.pinProblem()).toBe('kiosks.invalid.pin');

    component.pin.set('482915');
    component.pinConfirm.set('482916');
    expect(component.pinProblem()).toBe('kiosks.invalid.pinMismatch');
  });

  it('checks the code format only when one is given', () => {
    fillIn();

    component.code.set('K-1');
    expect(component.generalProblems()).toContain('kiosks.invalid.code');

    component.code.set('');
    expect(component.canCreate()).toBe(true);
  });

  it('creates the kiosk with "default" limits sent as null', async () => {
    fillIn();

    const creating = component.create();
    const request = backend.expectOne('/api/admin/kiosks');

    expect(request.request.method).toBe('POST');
    expect(request.request.headers.has('Idempotency-Key')).toBe(true);
    expect(request.request.body).toMatchObject({
      name: 'Nizami küçəsi',
      code: null,
      lat: 40.4093,
      lng: 49.8671,
      maxFileSizeMb: null,
      maxPages: null,
      adminPin: '482915',
    });

    request.flush({ id: 'k1', code: 'KSK-0003' });
    await creating;
  });
});
