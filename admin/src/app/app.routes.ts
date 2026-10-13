import { Routes } from '@angular/router';

import { Permissions } from './core/auth/auth.models';
import { anonymousGuard, authGuard, permissionGuard } from './core/auth/auth.guards';

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
    loadComponent: () => import('./features/login/login.component').then((m) => m.LoginComponent),
  },
  {
    // Outside the shell: while a password change is outstanding there is no
    // menu to show, because nothing else may be reached (plan section 2.8).
    path: 'change-password',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/login/change-password.component').then((m) => m.ChangePasswordComponent),
  },
  {
    // The operator panel (plan section 8B): its own frame, made for a phone.
    // Each page carries the permission its own request needs, like the rest.
    path: 'operator',
    canActivate: [authGuard, permissionGuard(Permissions.operatorPanel)],
    loadComponent: () =>
      import('./features/operator/operator-shell.component').then((m) => m.OperatorShellComponent),
    children: [
      {
        path: '',
        pathMatch: 'full',
        loadComponent: () =>
          import('./features/operator/operator-kiosks.component').then((m) => m.OperatorKiosksComponent),
      },
      {
        path: 'kiosks/:id',
        loadComponent: () =>
          import('./features/operator/operator-kiosk.component').then((m) => m.OperatorKioskComponent),
      },
      {
        path: 'activate',
        canActivate: [permissionGuard(Permissions.kiosksActivate)],
        loadComponent: () =>
          import('./features/operator/operator-activate.component').then((m) => m.OperatorActivateComponent),
      },
      {
        path: 'tasks',
        canActivate: [permissionGuard(Permissions.serviceTasksView)],
        loadComponent: () =>
          import('./features/operator/operator-tasks.component').then((m) => m.OperatorTasksComponent),
      },
      {
        path: 'incidents',
        canActivate: [permissionGuard(Permissions.incidentsView)],
        loadComponent: () =>
          import('./features/operator/operator-incidents.component').then((m) => m.OperatorIncidentsComponent),
      },
      {
        path: 'inventory',
        canActivate: [permissionGuard(Permissions.inventoryView)],
        loadComponent: () =>
          import('./features/operator/operator-inventory.component').then((m) => m.OperatorInventoryComponent),
      },
    ],
  },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./core/layout/shell.component').then((m) => m.ShellComponent),
    children: [
      {
        path: '',
        pathMatch: 'full',
        loadComponent: () =>
          import('./features/dashboard/dashboard.component').then((m) => m.DashboardComponent),
      },
      {
        path: 'devices',
        canActivate: [permissionGuard(Permissions.devicesView)],
        loadComponent: () =>
          import('./features/devices/devices.component').then((m) => m.DevicesComponent),
      },
      {
        path: 'roles',
        canActivate: [permissionGuard(Permissions.rolesManage)],
        loadComponent: () =>
          import('./features/roles/roles.component').then((m) => m.RolesComponent),
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
          import('./features/admin-users/admin-users.component').then((m) => m.AdminUsersComponent),
      },
      {
        path: 'kiosks',
        canActivate: [permissionGuard(Permissions.kiosksView)],
        loadComponent: () =>
          import('./features/kiosks/kiosks.component').then((m) => m.KiosksComponent),
      },
      {
        path: 'kiosks/new',
        canActivate: [permissionGuard(Permissions.kiosksCreate)],
        loadComponent: () =>
          import('./features/kiosks/kiosk-editor.component').then((m) => m.KioskEditorComponent),
      },
      {
        path: 'kiosks/:id',
        canActivate: [permissionGuard(Permissions.kiosksView)],
        loadComponent: () =>
          import('./features/kiosks/kiosk-editor.component').then((m) => m.KioskEditorComponent),
      },
      {
        path: 'print-jobs',
        canActivate: [permissionGuard(Permissions.jobsView)],
        loadComponent: () =>
          import('./features/print-jobs/print-jobs.component').then((m) => m.PrintJobsComponent),
      },
      {
        path: 'print-jobs/:id',
        canActivate: [permissionGuard(Permissions.jobsView)],
        loadComponent: () =>
          import('./features/print-jobs/print-job-detail.component').then(
            (m) => m.PrintJobDetailComponent,
          ),
      },
      // Step 4: money and customers (plan section 8A.3).
      {
        path: 'payments',
        canActivate: [permissionGuard(Permissions.paymentsView)],
        loadComponent: () => import('./features/money/payments.component').then((m) => m.PaymentsComponent),
      },
      {
        path: 'payments/:id',
        canActivate: [permissionGuard(Permissions.paymentsView)],
        loadComponent: () => import('./features/money/payment-detail.component').then((m) => m.PaymentDetailComponent),
      },
      {
        path: 'refunds',
        canActivate: [permissionGuard(Permissions.refundsView)],
        loadComponent: () => import('./features/money/refunds.component').then((m) => m.RefundsComponent),
      },
      {
        path: 'fiscal',
        canActivate: [permissionGuard(Permissions.fiscalView)],
        loadComponent: () => import('./features/money/fiscal.component').then((m) => m.FiscalComponent),
      },
      {
        path: 'receipts',
        canActivate: [permissionGuard(Permissions.receiptsView)],
        loadComponent: () => import('./features/money/receipts.component').then((m) => m.ReceiptsComponent),
      },
      {
        path: 'vouchers',
        canActivate: [permissionGuard(Permissions.vouchersView)],
        loadComponent: () => import('./features/money/vouchers.component').then((m) => m.VouchersComponent),
      },
      {
        path: 'promos',
        canActivate: [permissionGuard(Permissions.promosManage)],
        loadComponent: () => import('./features/money/promos.component').then((m) => m.PromosComponent),
      },
      // Step 6: the referral programme (plan section 8A.3).
      {
        path: 'referrals',
        canActivate: [permissionGuard(Permissions.referralsView)],
        loadComponent: () => import('./features/referrals/referrals.component').then((m) => m.ReferralsComponent),
      },
      {
        path: 'customers',
        canActivate: [permissionGuard(Permissions.usersView)],
        loadComponent: () => import('./features/money/customers.component').then((m) => m.CustomersComponent),
      },
      {
        path: 'customers/:id',
        canActivate: [permissionGuard(Permissions.usersView)],
        loadComponent: () => import('./features/money/customer-detail.component').then((m) => m.CustomerDetailComponent),
      },
      {
        path: 'suspected',
        canActivate: [permissionGuard(Permissions.jobsView)],
        loadComponent: () => import('./features/money/suspected.component').then((m) => m.SuspectedComponent),
      },
      {
        path: 'revenue',
        canActivate: [permissionGuard(Permissions.reportsView)],
        loadComponent: () => import('./features/money/revenue.component').then((m) => m.RevenueComponent),
      },
      {
        path: 'pricing',
        canActivate: [permissionGuard(Permissions.pricingManage)],
        loadComponent: () =>
          import('./features/pricing/pricing.component').then((m) => m.PricingComponent),
      },
      {
        path: 'formats',
        canActivate: [permissionGuard(Permissions.formatsManage)],
        loadComponent: () =>
          import('./features/file-formats/file-formats.component').then(
            (m) => m.FileFormatsComponent,
          ),
      },
      {
        path: 'storage',
        canActivate: [permissionGuard(Permissions.storageManage)],
        loadComponent: () =>
          import('./features/storage/storage.component').then((m) => m.StorageComponent),
      },
      {
        path: 'settings',
        canActivate: [permissionGuard(Permissions.settingsManage)],
        loadComponent: () =>
          import('./features/settings/settings.component').then((m) => m.SettingsComponent),
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
