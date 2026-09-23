import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService } from '../../core/api/admin-api.service';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { HasPermissionDirective } from '../../core/auth/has-permission.directive';

/**
 * The dashboard (plan section 8A.3).
 *
 * Step 1 shows what exists so far: who is signed in, what they may do, their
 * kiosk scope, and how many devices are waiting to be activated. The kiosk
 * status cards, incidents and revenue chart arrive with the features they
 * report on.
 */
@Component({
  selector: 'app-dashboard',
  imports: [
    RouterLink,
    MatCardModule,
    MatIconModule,
    MatButtonModule,
    MatChipsModule,
    TranslatePipe,
    HasPermissionDirective,
  ],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss',
})
export class DashboardComponent {
  private readonly auth = inject(AuthService);
  private readonly api = inject(AdminApiService);

  readonly permissions = Permissions;
  readonly admin = this.auth.admin;

  readonly pendingDeviceCount = signal<number | null>(null);

  readonly kioskScopeLabel = computed(() => {
    const admin = this.admin();

    if (!admin) {
      return '';
    }

    return admin.allKiosks ? 'dashboard.allKiosks' : 'dashboard.scopedKiosks';
  });

  readonly kioskScopeCount = computed(() => this.admin()?.kioskIds.length ?? 0);

  constructor() {
    // Only fetched when the account may see devices at all; asking otherwise
    // would produce a 403 in the console for no reason.
    if (this.auth.can(Permissions.devicesView)) {
      void this.loadPendingDevices();
    }
  }

  private async loadPendingDevices(): Promise<void> {
    try {
      const devices = await firstValueFrom(this.api.listDevices('Pending'));
      this.pendingDeviceCount.set(devices.length);
    } catch {
      // A dashboard tile that cannot load should stay blank rather than take
      // the page down with it.
      this.pendingDeviceCount.set(null);
    }
  }
}
