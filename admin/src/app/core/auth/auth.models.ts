/**
 * The signed-in administrator, with their effective permissions and kiosk
 * scope (plan section 8A.2).
 */
export interface AdminSummary {
  readonly id: string;
  readonly email: string;
  readonly fullName: string;
  readonly roles: readonly string[];

  /** The union of the permissions of every role held. */
  readonly permissions: readonly string[];

  /** Whether this account may act on every kiosk, present and future. */
  readonly allKiosks: boolean;

  /** The kiosks in scope when {@link allKiosks} is false. */
  readonly kioskIds: readonly string[];

  /**
   * Set for the seeded SuperAdmin and after an administrator resets a
   * password. The router keeps the panel on the password screen until it
   * clears (plan section 2.8).
   */
  readonly mustChangePassword: boolean;
}

/**
 * What a successful sign-in returns.
 *
 * The refresh token is deliberately absent: it arrives as an HttpOnly cookie
 * so no script in the panel can read it (plan section 8A.1).
 */
export interface AdminLoginResponse {
  readonly accessToken: string;
  readonly accessTokenExpiresAt: string;
  readonly admin: AdminSummary;
}

/** A permission group, as the role editor lays it out (plan section 8A.2). */
export interface PermissionGroup {
  readonly key: string;
  readonly title: string;
  readonly permissions: readonly string[];
}

/** The permission names the panel checks against by name. */
export const Permissions = {
  dashboardView: 'dashboard.view',

  kiosksView: 'kiosks.view',
  kiosksCreate: 'kiosks.create',
  kiosksEdit: 'kiosks.edit',
  kiosksDelete: 'kiosks.delete',
  kiosksActivate: 'kiosks.activate',
  kioskLimitsManage: 'kiosks.limits.manage',
  devicesView: 'devices.view',
  devicesRevoke: 'devices.revoke',
  devicesEnroll: 'devices.enroll',

  settingsManage: 'settings.manage',
  pricingManage: 'pricing.manage',
  formatsManage: 'formats.manage',
  fiscalManage: 'fiscal.manage',
  storageManage: 'storage.manage',
  featuresManage: 'features.manage',
  legalManage: 'legal.manage',

  adminsManage: 'admins.manage',
  rolesManage: 'roles.manage',
  auditView: 'audit.view',

  operatorPanel: 'operator.panel',

  printersView: 'printers.view',
  printersSelect: 'printers.select',
  printerCapabilitiesManage: 'printers.capabilities.manage',
  printersRefresh: 'printers.refresh',
  printersTestPrint: 'printers.testprint',
  jobsView: 'jobs.view',
  jobsCancel: 'jobs.cancel',
  suppliesView: 'supplies.view',
  paperRefill: 'paper.refill',

  // Step 4: money and customers (plan section 8A.2).
  reportsView: 'reports.view',
  paymentsView: 'payments.view',
  paymentsRefund: 'payments.refund',
  refundsView: 'refunds.view',
  refundsRetry: 'refunds.retry',
  receiptsView: 'receipts.view',
  vouchersView: 'vouchers.view',
  fiscalView: 'fiscal.view',
  incidentsView: 'incidents.view',
  usersView: 'users.view',
  usersBlock: 'users.block',
  walletsAdjust: 'wallets.adjust',
  promosManage: 'promos.manage',
} as const;
