import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
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

/** Lets the pending promise chain run: the profile request follows the token exchange. */
async function settle(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
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

    http.expectOne('/api/auth/admin/login').flush(loginResponse());

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
    const stored = [...Object.values(localStorage), ...Object.values(sessionStorage)].join('|');

    expect(stored).not.toContain('header.payload.signature');
  });

  it('answers permission questions from the account it was given', async () => {
    const signedIn = service.signIn('operator@neoprint.test', 'password').toPromise();

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

    http.expectOne('/api/auth/admin/refresh').flush(loginResponse());
    await settle();

    http.expectOne('/api/admin/me').flush('', { status: 500, statusText: 'Server Error' });

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

    http.expectOne('/api/auth/admin/refresh').flush(loginResponse());
    await settle();

    http.expectOne('/api/admin/me').flush(admin({ permissions: ['devices.view', 'roles.manage'] }));

    await reloaded;

    expect(service.can('roles.manage')).toBe(true);
  });

  it('exchanges the token before reading the new permissions', async () => {
    const signedIn = service.signIn('admin@neoprint.test', 'password').toPromise();
    http.expectOne('/api/auth/admin/login').flush(loginResponse());
    await signedIn;

    const reloaded = service.reloadPermissions();

    // The API confirms the permission claim in the token as well as the
    // resolver, so a permission just granted needs a new token. Reading the
    // profile alone would open a menu item whose requests are still refused.
    const refresh = http.expectOne('/api/auth/admin/refresh');

    expect(refresh.request.method).toBe('POST');

    refresh.flush({
      ...loginResponse(),
      accessToken: 'token-with-the-new-permission',
    });

    await settle();

    http
      .expectOne('/api/admin/me')
      .flush(admin({ permissions: ['devices.view', 'settings.manage'] }));

    await reloaded;

    expect(service.token).toBe('token-with-the-new-permission');
    expect(service.can('settings.manage')).toBe(true);
  });

  it('still shows the new permissions when the token exchange fails', async () => {
    const signedIn = service.signIn('admin@neoprint.test', 'password').toPromise();
    http.expectOne('/api/auth/admin/login').flush(loginResponse());
    await signedIn;

    const reloaded = service.reloadPermissions();

    http
      .expectOne('/api/auth/admin/refresh')
      .flush('', { status: 500, statusText: 'Server Error' });

    await settle();

    // The interceptor exchanges the token on the next 401; the menu should not
    // be left behind in the meantime.
    http
      .expectOne('/api/admin/me')
      .flush(admin({ permissions: ['devices.view', 'settings.manage'] }));

    await reloaded;

    expect(service.can('settings.manage')).toBe(true);
    expect(service.isSignedIn()).toBe(true);
  });

  it('clears the session locally even when the logout call fails', async () => {
    const signedIn = service.signIn('admin@neoprint.test', 'password').toPromise();
    http.expectOne('/api/auth/admin/login').flush(loginResponse());
    await signedIn;

    const signedOut = service.signOut();

    http.expectOne('/api/auth/admin/logout').error(new ProgressEvent('network error'));

    await signedOut;

    expect(service.isSignedIn()).toBe(false);
    expect(service.token).toBeNull();
  });

  // ---------------------------------------------------------------------
  // The token for the live connection (T-248)
  // ---------------------------------------------------------------------

  /** Signs in with a token that runs out `secondsLeft` from now. */
  function signInFor(secondsLeft: number): void {
    service.signIn('admin@neoprint.test', 'pw').subscribe();

    http.expectOne('/api/auth/admin/login').flush({
      ...loginResponse(),
      accessTokenExpiresAt: new Date(Date.now() + secondsLeft * 1000).toISOString(),
    });
  }

  it('hands the live connection the token it holds while that token is good', async () => {
    signInFor(600);

    expect(await service.freshToken()).toBe('header.payload.signature');

    // Nothing was asked of the server for it.
    http.expectNone('/api/admin/me');
  });

  /** The server's answer to an exchange: another token, good for a quarter of an hour. */
  const exchanged = (): AdminLoginResponse => ({
    ...loginResponse(admin({ kioskIds: ['k-1'], allKiosks: false })),
    accessToken: 'second.token.value',
    accessTokenExpiresAt: new Date(Date.now() + 900_000).toISOString(),
  });

  // T-262: the first version asked for the profile and counted on a 401. For
  // half a minute after a token runs out the API still answers with it - and
  // the live connection, closed the moment it ran out, was opened with the
  // same token again, once a second.
  it('exchanges a token that has run out before it is handed to the live connection', async () => {
    signInFor(-10);

    const token = service.freshToken();

    http.expectOne('/api/auth/admin/refresh').flush(exchanged());

    expect(await token, 'the new token, not the one that ran out').toBe('second.token.value');
    expect(service.admin()?.kioskIds).toEqual(['k-1']);

    // Not "ask the API and see": the API would still have said yes.
    http.expectNone('/api/admin/me');
  });

  it('does the same for a token that is about to run out', async () => {
    signInFor(10);

    const token = service.freshToken();
    http.expectOne('/api/auth/admin/refresh').flush(exchanged());

    expect(await token).toBe('second.token.value');
  });

  it('hands the next connection the exchanged token without asking again', async () => {
    signInFor(-10);

    const first = service.freshToken();
    http.expectOne('/api/auth/admin/refresh').flush(exchanged());
    await first;

    expect(await service.freshToken()).toBe('second.token.value');
    http.expectNone('/api/auth/admin/refresh');
  });

  it('makes one exchange for everything that asks at the same moment', async () => {
    signInFor(-10);

    const first = service.freshToken();
    const second = service.freshToken();

    // The refresh token rotates on use: a second exchange beside the first
    // would be a replay, and a replay signs the administrator out.
    const requests = http.match('/api/auth/admin/refresh');
    expect(requests).toHaveLength(1);
    requests[0].flush(exchanged());

    expect(await first).toBe('second.token.value');
    expect(await second).toBe('second.token.value');
  });

  it('ends the session when the exchange is refused', async () => {
    signInFor(-10);

    const token = service.freshToken();
    http.expectOne('/api/auth/admin/refresh').flush('', { status: 401, statusText: 'Unauthorized' });

    expect(await token).toBe('');
    expect(service.isSignedIn()).toBe(false);
  });

  it('keeps the session when the exchange fails for another reason, so that it is tried again', async () => {
    signInFor(-10);

    const token = service.freshToken();
    http.expectOne('/api/auth/admin/refresh').flush('', { status: 429, statusText: 'Too Many Requests' });

    expect(await token, 'the attempt to connect fails and is made again later').toBe('header.payload.signature');
    expect(service.isSignedIn()).toBe(true);

    // The next attempt asks again rather than remembering the failure.
    const again = service.freshToken();
    http.expectOne('/api/auth/admin/refresh').flush(exchanged());

    expect(await again).toBe('second.token.value');
  });

  it('gives the connection no token when nobody is signed in', async () => {
    expect(await service.freshToken()).toBe('');

    http.expectNone('/api/admin/me');
  });

  it('a profile that cannot be re-read leaves the one on screen', async () => {
    signInFor(600);

    const reload = service.reloadProfile();
    http.expectOne('/api/admin/me').flush(null, { status: 503, statusText: 'Unavailable' });
    await reload;

    expect(service.admin()?.email).toBe('admin@neoprint.test');
  });
});
