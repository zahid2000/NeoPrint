import { Routes } from '@angular/router';

import { Permissions } from './core/auth/auth.models';
import {
  anonymousGuard,
  authGuard,
  permissionGuard,
} from './core/auth/auth.guards';

/**
 * The panel's routes (plan section 8A.3).
 *
 * Every page is lazily loaded, and every page behind the shell carries both
 * the sign-in guard and the permission its endpoints require. The guard is the
 * presentation half; the server enforces the same permission on each request
 * (plan section 8A.2).
 */
export const routes: Routes = [
  {
    path: 'login',
    canActivate: [anonymousGuard],
    loadComponent: () =>
      import('./features/login/login.component').then((m) => m.LoginComponent),
  },
  {
    // Outside the shell: while a password change is outstanding there is no
    // menu to show, because nothing else may be reached (plan section 2.8).
    path: 'change-password',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/login/change-password.component').then(
        (m) => m.ChangePasswordComponent,
      ),
  },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./core/layout/shell.component').then((m) => m.ShellComponent),
    children: [
      {
        path: '',
        pathMatch: 'full',
        loadComponent: () =>
          import('./features/dashboard/dashboard.component').then(
            (m) => m.DashboardComponent,
          ),
      },
      {
        path: 'devices',
        canActivate: [permissionGuard(Permissions.devicesView)],
        loadComponent: () =>
          import('./features/devices/devices.component').then(
            (m) => m.DevicesComponent,
          ),
      },
      {
        path: 'roles',
        canActivate: [permissionGuard(Permissions.rolesManage)],
        loadComponent: () =>
          import('./features/roles/roles.component').then(
            (m) => m.RolesComponent,
          ),
      },
      {
        path: 'features',
        canActivate: [permissionGuard(Permissions.featuresManage)],
        loadComponent: () =>
          import('./features/feature-flags/feature-flags.component').then(
            (m) => m.FeatureFlagsComponent,
          ),
      },
      {
        path: 'admins',
        canActivate: [permissionGuard(Permissions.adminsManage)],
        loadComponent: () =>
          import('./features/admin-users/admin-users.component').then(
            (m) => m.AdminUsersComponent,
          ),
      },
      {
        path: 'settings',
        canActivate: [permissionGuard(Permissions.settingsManage)],
        loadComponent: () =>
          import('./features/settings/settings.component').then(
            (m) => m.SettingsComponent,
          ),
      },
      {
        path: 'legal',
        canActivate: [permissionGuard(Permissions.legalManage)],
        loadComponent: () =>
          import('./features/legal-documents/legal-documents.component').then(
            (m) => m.LegalDocumentsComponent,
          ),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
