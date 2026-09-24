import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { describe, beforeEach, afterEach, expect, it, vi } from 'vitest';

import { RoleResponse } from '../../core/api/admin-api.service';
import { AdminSummary, PermissionGroup } from '../../core/auth/auth.models';
import { RolesComponent } from './roles.component';

/** The account doing the editing, as /api/admin/me reports it back. */
const signedInAdmin: AdminSummary = {
  id: '8f5d4f7e-0000-0000-0000-000000000001',
  email: 'admin@neoprint.test',
  fullName: 'Seeded Admin',
  roles: ['SuperAdmin'],
  permissions: ['kiosks.view', 'kiosks.activate', 'devices.view', 'roles.manage'],
  allKiosks: true,
  kioskIds: [],
  mustChangePassword: false,
};

const groups: PermissionGroup[] = [
  {
    key: 'kiosks',
    title: 'Kiosklar',
    permissions: ['kiosks.view', 'kiosks.activate', 'devices.view'],
  },
  { key: 'system', title: 'Sistem', permissions: ['roles.manage', 'audit.view'] },
];

const roles: RoleResponse[] = [
  {
    id: '11111111-1111-1111-1111-111111111111',
    name: 'SuperAdmin',
    description: 'Everything',
    permissions: ['kiosks.view', 'kiosks.activate', 'devices.view', 'roles.manage'],
    isSystem: true,
    userCount: 1,
    updatedAt: new Date().toISOString(),
  },
  {
    id: '22222222-2222-2222-2222-222222222222',
    name: 'Operator',
    description: 'Shop floor',
    permissions: ['kiosks.view'],
    isSystem: false,
    userCount: 3,
    updatedAt: new Date().toISOString(),
  },
];

/** Lets the promise chain inside a component method finish. */
async function flushMicrotasks(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

describe('RolesComponent', () => {
  let fixture: ComponentFixture<RolesComponent>;
  let component: RolesComponent;
  let backend: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'login', children: [] }]),
        provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(RolesComponent);
    component = fixture.componentInstance;

    backend.expectOne('/api/admin/roles').flush(roles);
    backend.expectOne('/api/admin/permissions').flush(groups);

    await fixture.whenStable();
  });

  afterEach(() => {
    backend.verify();
  });

  it('selects the first role and fills the editor from it', () => {
    expect(component.selected()?.name).toBe('SuperAdmin');
    expect(component.form.getRawValue().name).toBe('SuperAdmin');
    expect(component.draftCount()).toBe(4);
  });

  it('treats a system role as read-only (plan section 8A.2)', () => {
    expect(component.isReadOnly()).toBe(true);

    component.select('22222222-2222-2222-2222-222222222222');

    expect(component.isReadOnly()).toBe(false);
  });

  it('reports how much of a group is ticked', () => {
    component.select('22222222-2222-2222-2222-222222222222');

    expect(component.groupState(groups[0])).toBe('some');
    expect(component.groupState(groups[1])).toBe('none');

    component.select('11111111-1111-1111-1111-111111111111');

    expect(component.groupState(groups[0])).toBe('all');
    expect(component.groupState(groups[1])).toBe('some');
  });

  it('ticks and unticks a whole group at once', () => {
    component.select('22222222-2222-2222-2222-222222222222');

    component.toggleGroup(groups[0], true);

    expect(component.groupState(groups[0])).toBe('all');
    expect(component.has('devices.view')).toBe(true);

    component.toggleGroup(groups[0], false);

    expect(component.groupState(groups[0])).toBe('none');
    expect(component.has('kiosks.view')).toBe(false);
  });

  it('leaves other groups alone when a group is toggled', () => {
    component.select('11111111-1111-1111-1111-111111111111');

    component.toggleGroup(groups[0], false);

    expect(component.has('roles.manage')).toBe(true);
  });

  it('toggles a single permission', () => {
    component.select('22222222-2222-2222-2222-222222222222');

    component.toggle('audit.view', true);
    expect(component.has('audit.view')).toBe(true);

    component.toggle('audit.view', false);
    expect(component.has('audit.view')).toBe(false);
  });

  it('starts a new role with nothing ticked', () => {
    component.startNew();

    expect(component.selected()).toBeNull();
    expect(component.draftCount()).toBe(0);
    expect(component.form.getRawValue().name).toBe('');
  });

  it('creates a role when none is selected', async () => {
    component.startNew();
    component.form.setValue({ name: 'Refunds', description: 'Money back' });
    component.toggle('audit.view', true);

    const saved = component.save();

    const request = backend.expectOne('/api/admin/roles');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      name: 'Refunds',
      description: 'Money back',
      permissions: ['audit.view'],
    });

    request.flush({
      id: '33333333-3333-3333-3333-333333333333',
      name: 'Refunds',
      description: 'Money back',
      permissions: ['audit.view'],
      isSystem: false,
      userCount: 0,
      updatedAt: new Date().toISOString(),
    });

    await flushMicrotasks();

    // save() reloads the list and then the signed-in account's own permissions.
    backend.expectOne('/api/admin/roles').flush(roles);
    backend.expectOne('/api/admin/permissions').flush(groups);

    await flushMicrotasks();

    // The account's own permissions are re-read after a new token: a
    // permission it has just granted itself is not in the one it holds.
    backend.expectOne('/api/auth/admin/refresh').flush({
      accessToken: 'refreshed',
      accessTokenExpiresAt: new Date(Date.now() + 900_000).toISOString(),
      admin: signedInAdmin,
    });

    await flushMicrotasks();

    backend.expectOne('/api/admin/me').flush(signedInAdmin);

    await saved;
  });

  it('updates the selected role rather than creating another', async () => {
    component.select('22222222-2222-2222-2222-222222222222');
    component.toggle('devices.view', true);

    const saved = component.save();

    const request = backend.expectOne(
      '/api/admin/roles/22222222-2222-2222-2222-222222222222',
    );
    expect(request.request.method).toBe('PUT');

    request.flush({ ...roles[1], permissions: ['kiosks.view', 'devices.view'] });

    await flushMicrotasks();

    backend.expectOne('/api/admin/roles').flush(roles);
    backend.expectOne('/api/admin/permissions').flush(groups);

    await flushMicrotasks();

    // The account's own permissions are re-read after a new token: a
    // permission it has just granted itself is not in the one it holds.
    backend.expectOne('/api/auth/admin/refresh').flush({
      accessToken: 'refreshed',
      accessTokenExpiresAt: new Date(Date.now() + 900_000).toISOString(),
      admin: signedInAdmin,
    });

    await flushMicrotasks();

    backend.expectOne('/api/admin/me').flush(signedInAdmin);

    await saved;
  });

  it('refuses to save a system role', async () => {
    component.select('11111111-1111-1111-1111-111111111111');

    await component.save();

    // Nothing was sent: SuperAdmin must keep every permission, or an
    // installation could lock itself out of its own panel.
    backend.expectNone('/api/admin/roles/11111111-1111-1111-1111-111111111111');
  });

  it('shows the message the server sent when a delete is refused', async () => {
    component.select('22222222-2222-2222-2222-222222222222');

    // jsdom has no dialogs; the component asks before a destructive action.
    vi.spyOn(window, 'confirm').mockReturnValue(true);

    const removed = component.remove(roles[1]);

    backend
      .expectOne('/api/admin/roles/22222222-2222-2222-2222-222222222222')
      .flush(
        { detail: 'Bu rol 3 istifadəçiyə verilib.' },
        { status: 409, statusText: 'Conflict' },
      );

    await removed;

    expect(component.errorMessage()).toBe('Bu rol 3 istifadəçiyə verilib.');
  });
});
