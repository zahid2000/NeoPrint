import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { describe, beforeEach, afterEach, expect, it } from 'vitest';

import { AdminLoginResponse, AdminSummary } from './auth.models';
import { AuthService } from './auth.service';

function admin(overrides: Partial<AdminSummary> = {}): AdminSummary {
  return {
    id: '8f5d4f7e-0000-0000-0000-000000000001',
    email: 'admin@neoprint.test',
    fullName: 'Seeded Admin',
    roles: ['SuperAdmin'],
    permissions: ['devices.view', 'kiosks.activate'],
    allKiosks: true,
    kioskIds: [],
    mustChangePassword: false,
    ...overrides,
  };
}

function loginResponse(summary = admin()): AdminLoginResponse {
  return {
    accessToken: 'header.payload.signature',
    accessTokenExpiresAt: new Date(Date.now() + 900_000).toISOString(),
    admin: summary,
  };
}

describe('AuthService', () => {
  let service: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // Signing out navigates, so the routes it uses have to exist here.
        provideRouter([
          { path: 'login', children: [] },
          { path: '', children: [] },
        ]),
      ],
    });

    service = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
  });

  it('holds no session before signing in', () => {
    expect(service.isSignedIn()).toBe(false);
    expect(service.token).toBeNull();
    expect(service.can('devices.view')).toBe(false);
  });

  it('applies the account and the token on sign-in', async () => {
    const signedIn = service.signIn('admin@neoprint.test', 'password').toPromise();

    http
      .expectOne('/api/auth/admin/login')
      .flush(loginResponse());

    await signedIn;

    expect(service.isSignedIn()).toBe(true);
    expect(service.token).toBe('header.payload.signature');
    expect(service.admin()?.email).toBe('admin@neoprint.test');
  });

  it('never writes the access token to browser storage', async () => {
    const signedIn = service.signIn('admin@neoprint.test', 'password').toPromise();
    http.expectOne('/api/auth/admin/login').flush(loginResponse());
    await signedIn;

    // The token lives fifteen minutes and any script on the page can read
    // storage, so it is deliberately kept in memory only (plan section 8A.1).
    const stored = [
      ...Object.values(localStorage),
      ...Object.values(sessionStorage),
    ].join('|');

    expect(stored).not.toContain('header.payload.signature');
  });

  it('answers permission questions from the account it was given', async () => {
    const signedIn = service
      .signIn('operator@neoprint.test', 'password')
      .toPromise();

    http.expectOne('/api/auth/admin/login').flush(
      loginResponse(
        admin({
          permissions: ['devices.view'],
          allKiosks: false,
          kioskIds: ['11111111-1111-1111-1111-111111111111'],
        }),
      ),
    );

    await signedIn;

    expect(service.can('devices.view')).toBe(true);
    expect(service.can('roles.manage')).toBe(false);
    expect(service.canAny(['roles.manage', 'devices.view'])).toBe(true);
    expect(service.canAny(['roles.manage'])).toBe(false);

    expect(service.canSeeKiosk('11111111-1111-1111-1111-111111111111')).toBe(true);
    expect(service.canSeeKiosk('22222222-2222-2222-2222-222222222222')).toBe(false);
  });

  it('lets an unscoped account see every kiosk', async () => {
    const signedIn = service.signIn('admin@neoprint.test', 'password').toPromise();
    http.expectOne('/api/auth/admin/login').flush(loginResponse());
    await signedIn;

    expect(service.canSeeKiosk('99999999-9999-9999-9999-999999999999')).toBe(true);
  });

  it('reports an outstanding password change', async () => {
    const signedIn = service.signIn('admin@neoprint.test', 'password').toPromise();

    http
      .expectOne('/api/auth/admin/login')
      .flush(loginResponse(admin({ mustChangePassword: true })));

    await signedIn;

    expect(service.mustChangePassword()).toBe(true);
  });

  it('restores a session from the refresh cookie at startup', async () => {
    const restored = service.restore();

    http.expectOne('/api/auth/admin/refresh').flush(loginResponse());

    await restored;

    expect(service.isSignedIn()).toBe(true);
    expect(service.isRestoring()).toBe(false);
  });

  it('treats a refused refresh at startup as no session, not an error', async () => {
    const restored = service.restore();

    http
      .expectOne('/api/auth/admin/refresh')
      .flush('', { status: 401, statusText: 'Unauthorized' });

    await restored;

    expect(service.isSignedIn()).toBe(false);
    expect(service.isRestoring()).toBe(false);
  });

  it('keeps the previous permissions when a reload fails', async () => {
    const signedIn = service.signIn('admin@neoprint.test', 'password').toPromise();
    http.expectOne('/api/auth/admin/login').flush(loginResponse());
    await signedIn;

    const reloaded = service.reloadPermissions();

    http
      .expectOne('/api/admin/me')
      .flush('', { status: 500, statusText: 'Server Error' });

    await reloaded;

    // The server enforces permissions on every request, so a stale menu is
    // cosmetic - losing the session over it would not be.
    expect(service.can('devices.view')).toBe(true);
  });

  it('picks up a changed role without signing in again', async () => {
    const signedIn = service.signIn('admin@neoprint.test', 'password').toPromise();
    http.expectOne('/api/auth/admin/login').flush(loginResponse());
    await signedIn;

    const reloaded = service.reloadPermissions();

    http
      .expectOne('/api/admin/me')
      .flush(admin({ permissions: ['devices.view', 'roles.manage'] }));

    await reloaded;

    expect(service.can('roles.manage')).toBe(true);
  });

  it('clears the session locally even when the logout call fails', async () => {
    const signedIn = service.signIn('admin@neoprint.test', 'password').toPromise();
    http.expectOne('/api/auth/admin/login').flush(loginResponse());
    await signedIn;

    const signedOut = service.signOut();

    http
      .expectOne('/api/auth/admin/logout')
      .error(new ProgressEvent('network error'));

    await signedOut;

    expect(service.isSignedIn()).toBe(false);
    expect(service.token).toBeNull();
  });
});
