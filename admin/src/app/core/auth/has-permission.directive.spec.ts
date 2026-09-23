import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, beforeEach, afterEach, expect, it } from 'vitest';

import { AdminLoginResponse, AdminSummary } from './auth.models';
import { AuthService } from './auth.service';
import { HasPermissionDirective } from './has-permission.directive';

@Component({
  imports: [HasPermissionDirective],
  template: `
    <button *appHasPermission="'devices.revoke'" id="revoke">Detach</button>
    <button *appHasPermission="['roles.manage', 'admins.manage']" id="manage">
      Manage
    </button>
  `,
})
class HostComponent {}

function session(permissions: readonly string[]): AdminLoginResponse {
  const admin: AdminSummary = {
    id: '8f5d4f7e-0000-0000-0000-000000000001',
    email: 'admin@neoprint.test',
    fullName: 'Seeded Admin',
    roles: ['Operator'],
    permissions,
    allKiosks: true,
    kioskIds: [],
    mustChangePassword: false,
  };

  return {
    accessToken: 'token',
    accessTokenExpiresAt: new Date(Date.now() + 900_000).toISOString(),
    admin,
  };
}

describe('HasPermissionDirective', () => {
  let fixture: ComponentFixture<HostComponent>;
  let backend: HttpTestingController;
  let auth: AuthService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'login', children: [] }]),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    auth = TestBed.inject(AuthService);
    fixture = TestBed.createComponent(HostComponent);
  });

  afterEach(() => {
    backend.verify();
  });

  async function signIn(permissions: readonly string[]): Promise<void> {
    const signedIn = auth.signIn('admin@neoprint.test', 'password').toPromise();
    backend.expectOne('/api/auth/admin/login').flush(session(permissions));
    await signedIn;
  }

  function rendered(id: string): boolean {
    return fixture.nativeElement.querySelector(`#${id}`) !== null;
  }

  it('renders nothing for an account with no permissions', async () => {
    await fixture.whenStable();

    expect(rendered('revoke')).toBe(false);
    expect(rendered('manage')).toBe(false);
  });

  it('renders the control whose permission is held', async () => {
    await signIn(['devices.revoke']);
    await fixture.whenStable();

    expect(rendered('revoke')).toBe(true);
    expect(rendered('manage')).toBe(false);
  });

  it('accepts a list where any one permission will do', async () => {
    await signIn(['admins.manage']);
    await fixture.whenStable();

    expect(rendered('manage')).toBe(true);
  });

  it('follows a role change without the page being reloaded', async () => {
    await signIn(['devices.view']);
    await fixture.whenStable();

    expect(rendered('revoke')).toBe(false);

    // What the server announces over SignalR on a role change
    // (plan section 8A.2).
    const reloaded = auth.reloadPermissions();
    backend.expectOne('/api/admin/me').flush({
      ...session(['devices.view', 'devices.revoke']).admin,
    });
    await reloaded;

    await fixture.whenStable();

    expect(rendered('revoke')).toBe(true);
  });

  it('removes a control when the permission is taken away', async () => {
    await signIn(['devices.revoke']);
    await fixture.whenStable();

    expect(rendered('revoke')).toBe(true);

    const reloaded = auth.reloadPermissions();
    backend.expectOne('/api/admin/me').flush({ ...session([]).admin });
    await reloaded;

    await fixture.whenStable();

    expect(rendered('revoke')).toBe(false);
  });
});
