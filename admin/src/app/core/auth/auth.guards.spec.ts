import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  RouterStateSnapshot,
  UrlTree,
  provideRouter,
} from '@angular/router';
import { describe, beforeEach, afterEach, expect, it } from 'vitest';

import { anonymousGuard, authGuard, permissionGuard } from './auth.guards';
import { AdminLoginResponse, AdminSummary } from './auth.models';
import { AuthService } from './auth.service';

function session(overrides: Partial<AdminSummary> = {}): AdminLoginResponse {
  return {
    accessToken: 'token',
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
      ...overrides,
    },
  };
}

function state(url: string): RouterStateSnapshot {
  return { url } as RouterStateSnapshot;
}

const route = {} as ActivatedRouteSnapshot;

describe('route guards', () => {
  let auth: AuthService;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([
          { path: 'login', children: [] },
          { path: 'change-password', children: [] },
          { path: '', children: [] },
        ]),
      ],
    });

    auth = TestBed.inject(AuthService);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
  });

  async function signIn(overrides: Partial<AdminSummary> = {}): Promise<void> {
    const signedIn = auth.signIn('admin@neoprint.test', 'password').toPromise();
    backend.expectOne('/api/auth/admin/login').flush(session(overrides));
    await signedIn;
  }

  describe('authGuard', () => {
    it('sends an anonymous visitor to sign-in and remembers where they were going', () => {
      const result = TestBed.runInInjectionContext(() =>
        authGuard(route, state('/devices')),
      ) as UrlTree;

      expect(result).toBeInstanceOf(UrlTree);
      expect(result.toString()).toContain('/login');
      expect(result.queryParams['returnUrl']).toBe('/devices');
    });

    it('lets a signed-in administrator through', async () => {
      await signIn();

      expect(
        TestBed.runInInjectionContext(() => authGuard(route, state('/devices'))),
      ).toBe(true);
    });

    it('holds the panel on the password screen while a change is outstanding', async () => {
      await signIn({ mustChangePassword: true });

      const result = TestBed.runInInjectionContext(() =>
        authGuard(route, state('/devices')),
      ) as UrlTree;

      expect(result.toString()).toContain('/change-password');
    });

    it('does not redirect the password screen to itself', async () => {
      await signIn({ mustChangePassword: true });

      expect(
        TestBed.runInInjectionContext(() =>
          authGuard(route, state('/change-password')),
        ),
      ).toBe(true);
    });
  });

  describe('permissionGuard', () => {
    it('opens a page whose permission is held', async () => {
      await signIn();

      const guard = permissionGuard('devices.view');

      expect(TestBed.runInInjectionContext(() => guard(route, state('/devices')))).toBe(
        true,
      );
    });

    it('sends an account without the permission to the dashboard', async () => {
      await signIn({ permissions: ['dashboard.view'] });

      const guard = permissionGuard('roles.manage');

      const result = TestBed.runInInjectionContext(() =>
        guard(route, state('/roles')),
      ) as UrlTree;

      expect(result).toBeInstanceOf(UrlTree);
      expect(result.toString()).toBe('/');
    });

    it('accepts any one of several permissions', async () => {
      await signIn({ permissions: ['devices.enroll'] });

      const guard = permissionGuard('devices.view', 'devices.enroll');

      expect(TestBed.runInInjectionContext(() => guard(route, state('/devices')))).toBe(
        true,
      );
    });
  });

  describe('anonymousGuard', () => {
    it('lets an anonymous visitor reach sign-in', () => {
      expect(
        TestBed.runInInjectionContext(() => anonymousGuard(route, state('/login'))),
      ).toBe(true);
    });

    it('keeps a signed-in administrator off the sign-in screen', async () => {
      await signIn();

      const result = TestBed.runInInjectionContext(() =>
        anonymousGuard(route, state('/login')),
      ) as UrlTree;

      expect(result.toString()).toBe('/');
    });
  });
});
