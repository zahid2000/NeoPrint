import { Component } from '@angular/core';

import { PlaceholderComponent } from '../../core/layout/placeholder.component';

/**
 * Administrator accounts (plan sections 8A.2, 8A.3).
 *
 * The screen waits on the admin-user endpoints; the accounts themselves are
 * created by the seeder for now, and their roles and kiosk scope are already
 * enforced everywhere (plan section 8A.2).
 */
@Component({
  selector: 'app-admin-users',
  imports: [PlaceholderComponent],
  template: `
    <app-placeholder titleKey="nav.admins" icon="manage_accounts" />
  `,
})
export class AdminUsersComponent {}
