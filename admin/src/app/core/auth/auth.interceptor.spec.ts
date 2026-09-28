import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { describe, beforeEach, afterEach, expect, it } from 'vitest';

import { authInterceptor } from './auth.interceptor';
import { AdminLoginResponse } from './auth.models';
import { AuthService } from './auth.service';

const session: AdminLoginResponse = {
  accessToken: 'first-token',
  accessTokenExpiresAt: new Date(Date.now() + 900_000).toISOString(),
  admin: {
    id: '8f5d4f7e-0000-0000-0000-000000000001',
    email: 'admin@neoprint.test',
    fullName: 'Seeded Admin',
    roles: ['SuperAdmin'],
    permissions: ['devices.view'],
    allKiosks: true,
    kioskIds: [],
    mustChangePassword: false,
  },
};

const refreshed: AdminLoginResponse = { ...session, accessToken: 'second-token' };

describe('authInterceptor', () => {
  let http: HttpClient;
  let backend: HttpTestingController;
  let auth: AuthService;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
        provideRouter([
          { path: 'login', children: [] },
          { path: '', children: [] },
        ]),
      ],
    });

    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
    auth = TestBed.inject(AuthService);

    const signedIn = auth.signIn('admin@neoprint.test', 'password').toPromise();
    backend.expectOne('/api/auth/admin/login').flush(session);
    await signedIn;
  });

  afterEach(() => {
    backend.verify();
  });

  it('attaches the access token to an admin request', () => {
    http.get('/api/admin/devices').subscribe();

    const request = backend.expectOne('/api/admin/devices');

    expect(request.request.headers.get('Authorization')).toBe('Bearer first-token');
    expect(request.request.withCredentials).toBe(true);

    request.flush([]);
  });

  it('asks for messages in the language the panel shows', () => {
    TestBed.inject(TranslateService).use('ru');
    http.get('/api/admin/devices').subscribe();

    const request = backend.expectOne('/api/admin/devices');

    expect(request.request.headers.get('Accept-Language')).toBe('ru');

    request.flush([]);
  });

  it('sends no token to the sign-in endpoint', () => {
    // Even with a live session: the token belongs on admin calls, and an
    // Authorization header on sign-in would only be logged for nothing.
    http.post('/api/auth/admin/login', {}).subscribe();

    const request = backend.expectOne('/api/auth/admin/login');

    expect(request.request.headers.has('Authorization')).toBe(false);

    request.flush(session);
  });

  it('refreshes once and retries the request after a 401', async () => {
    const response = http.get('/api/admin/devices').toPromise();

    backend.expectOne('/api/admin/devices').flush('', { status: 401, statusText: 'Unauthorized' });

    backend.expectOne('/api/auth/admin/refresh').flush(refreshed);

    const retried = backend.expectOne('/api/admin/devices');

    // The retry has to carry the new token, not the one that was refused.
    expect(retried.request.headers.get('Authorization')).toBe('Bearer second-token');

    retried.flush([{ id: 'a' }]);

    await expect(response).resolves.toEqual([{ id: 'a' }]);
  });

  it('shares one refresh between requests that hit a 401 together', async () => {
    const first = http.get('/api/admin/devices').toPromise();
    const second = http.get('/api/admin/roles').toPromise();

    backend.expectOne('/api/admin/devices').flush('', { status: 401, statusText: 'Unauthorized' });
    backend.expectOne('/api/admin/roles').flush('', { status: 401, statusText: 'Unauthorized' });

    // One refresh, not two: the refresh token rotates on use, so a second
    // call would be treated as a replay and revoke the whole chain
    // (plan section 2.8).
    const refreshes = backend.match('/api/auth/admin/refresh');
    expect(refreshes).toHaveLength(1);

    refreshes[0].flush(refreshed);

    backend.expectOne('/api/admin/devices').flush([]);
    backend.expectOne('/api/admin/roles').flush([]);

    await Promise.all([first, second]);
  });

  it('signs the panel out when the refresh is refused too', async () => {
    const response = http.get('/api/admin/devices').toPromise();

    backend.expectOne('/api/admin/devices').flush('', { status: 401, statusText: 'Unauthorized' });

    backend
      .expectOne('/api/auth/admin/refresh')
      .flush('', { status: 401, statusText: 'Unauthorized' });

    await expect(response).rejects.toBeTruthy();

    expect(auth.isSignedIn()).toBe(false);
  });

  it('passes a 403 straight through without refreshing', async () => {
    const response = http.get('/api/admin/roles').toPromise();

    backend.expectOne('/api/admin/roles').flush('', { status: 403, statusText: 'Forbidden' });

    await expect(response).rejects.toBeTruthy();

    // A 403 means the permission is missing, which a new token cannot fix.
    backend.expectNone('/api/auth/admin/refresh');
    expect(auth.isSignedIn()).toBe(true);
  });
});
