import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { provideTranslateService, TranslateService } from '@ngx-translate/core';
import { of } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';

import { AdminKioskSession } from '../../core/api/admin-api.service';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { translations } from '../../core/i18n/translations';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { KioskSessionsComponent } from './kiosk-sessions.component';

const kioskId = '22222222-2222-2222-2222-222222222222';
const list = `/api/admin/kiosks/${kioskId}/sessions`;

const hub = {
  connectionState: (): 'connected' => 'connected',
  onState: (): (() => void) => () => undefined,
  onReconnect: (): (() => void) => () => undefined,
  on: (): (() => void) => () => undefined,
};

function session(overrides: Partial<AdminKioskSession> = {}): AdminKioskSession {
  return {
    id: 's-1',
    userId: 'u-1',
    userPublicId: 'NP-48213',
    openedAt: new Date(Date.now() - 2 * 60_000).toISOString(),
    expiresAt: new Date(Date.now() + 8 * 60_000 - 1_000).toISOString(),
    ...overrides,
  };
}

let backend: HttpTestingController;
let dialogAnswer: { reason: string } | undefined;

async function open(permissions: string[], shown: AdminKioskSession[]): Promise<ComponentFixture<KioskSessionsComponent>> {
  dialogAnswer = { reason: 'Kiosk yenilənməlidir' };
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      { provide: AdminHubService, useValue: hub },
      {
        provide: AuthService,
        useValue: { canAny: (wanted: readonly string[]) => wanted.some((permission) => permissions.includes(permission)) },
      },
      { provide: MatDialog, useValue: { open: () => ({ afterClosed: () => of(dialogAnswer) }) } },
    ],
  });
  const translate = TestBed.inject(TranslateService);
  translate.setTranslation('az', translations.az);
  translate.use('az');
  backend = TestBed.inject(HttpTestingController);

  const fixture = TestBed.createComponent(KioskSessionsComponent);
  fixture.componentRef.setInput('kioskId', kioskId);
  fixture.detectChanges();
  backend.expectOne(list).flush(shown);
  await settle(fixture);
  return fixture;
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  await fixture.whenStable();
  fixture.detectChanges();
}

function text(fixture: ComponentFixture<unknown>): string {
  return (fixture.nativeElement as HTMLElement).textContent ?? '';
}

describe('KioskSessionsComponent (T-362)', () => {
  afterEach(() => backend.verify());

  it('lists the phones joined to the kiosk by public id, with the minutes left, and links the customer for who may see them', async () => {
    const fixture = await open([Permissions.kiosksView, Permissions.usersView], [session()]);

    expect(text(fixture)).toContain('Açıq sessiyalar');
    expect(text(fixture)).toContain('8 dəq qalıb');
    const link = (fixture.nativeElement as HTMLElement).querySelector('a');
    expect(link?.textContent?.trim()).toBe('NP-48213');
    expect(link?.getAttribute('href')).toBe('/customers/u-1');
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-close]')).toBeNull();
  });

  it('closes a session with a reason, says so and reads the list again', async () => {
    const fixture = await open([Permissions.kiosksView, Permissions.kioskCommandsPause], [session()]);
    expect((fixture.nativeElement as HTMLElement).querySelector('a')).toBeNull();

    ((fixture.nativeElement as HTMLElement).querySelector('[data-close="s-1"]') as HTMLButtonElement).click();
    await settle(fixture);

    const close = backend.expectOne(`${list}/s-1/close`);
    expect(close.request.method).toBe('POST');
    expect(close.request.body).toEqual({ reason: 'Kiosk yenilənməlidir' });
    close.flush(null, { status: 204, statusText: 'No Content' });
    await settle(fixture);

    backend.expectOne(list).flush([]);
    await settle(fixture);
    expect(text(fixture)).toContain('Açıq sessiya yoxdur');
  });

  it('closes nothing when the dialog is cancelled', async () => {
    const fixture = await open([Permissions.kiosksView, Permissions.kioskCommandsPause], [session()]);
    dialogAnswer = undefined;

    ((fixture.nativeElement as HTMLElement).querySelector('[data-close="s-1"]') as HTMLButtonElement).click();
    await settle(fixture);

    backend.expectNone(`${list}/s-1/close`);
  });
});
