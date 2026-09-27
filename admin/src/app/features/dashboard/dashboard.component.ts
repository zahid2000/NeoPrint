import { Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService } from '../../core/api/admin-api.service';
import { AdminDashboard, StorageSyncJob } from '../../core/api/step2.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { HasPermissionDirective } from '../../core/auth/has-permission.directive';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { BytesPipe, CountPipe, DateTimePipe } from '../../shared/format.pipes';
import { StatusChipComponent } from '../../shared/status-chip.component';

/**
 * The dashboard (plan section 8A.3).
 *
 * It fills up step by step as the features it reports on arrive: step 1 the
 * devices waiting for activation and the kiosk scope, step 2 files,
 * conversion failures, storage health and sync. Kiosk statuses, incidents and
 * revenue come with steps 3-5. What an account may do is its roles' business
 * and is shown on the admin users page, not here.
 */
@Component({
  selector: 'app-dashboard',
  imports: [
    RouterLink,
    MatCardModule,
    MatIconModule,
    MatButtonModule,
    MatProgressBarModule,
    TranslatePipe,
    HasPermissionDirective,
    StatusChipComponent,
    BytesPipe,
    CountPipe,
    DateTimePipe,
  ],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss',
})
export class DashboardComponent {
  private readonly auth = inject(AuthService);
  private readonly api = inject(AdminApiService);
  private readonly hub = inject(AdminHubService);

  readonly permissions = Permissions;
  readonly admin = this.auth.admin;

  readonly pendingDeviceCount = signal<number | null>(null);
  readonly dashboard = signal<AdminDashboard | null>(null);
  readonly failed = signal(false);

  readonly kioskScopeLabel = computed(() => {
    const admin = this.admin();

    if (!admin) {
      return '';
    }

    return admin.allKiosks ? 'dashboard.allKiosks' : 'dashboard.scopedKiosks';
  });

  readonly kioskScopeCount = computed(() => this.admin()?.kioskIds.length ?? 0);

  /** Green at zero, red otherwise: a failure is something to look at. */
  readonly failuresStatus = computed(() =>
    (this.dashboard()?.files.failedLast24Hours ?? 0) === 0 ? 'Completed' : 'Failed',
  );

  constructor() {
    // Only fetched when the account may see devices at all; asking otherwise
    // would produce a 403 in the console for no reason.
    if (this.auth.can(Permissions.devicesView)) {
      void this.loadPendingDevices();
    }

    if (this.auth.can(Permissions.dashboardView)) {
      void this.load();

      // Sync progress and provider health arrive live; the figures are
      // fetched again when they change rather than recomputed here.
      const off = [
        this.hub.on('StorageSyncProgress', () => void this.load()),
        this.hub.on('StorageSyncCompleted', () => void this.load()),
        this.hub.on('StorageHealthChanged', () => void this.load()),
      ];
      inject(DestroyRef).onDestroy(() => off.forEach((unsubscribe) => unsubscribe()));
    }
  }

  percent(job: StorageSyncJob): number {
    return job.totalObjects > 0 ? Math.floor((job.copiedObjects / job.totalObjects) * 100) : 0;
  }

  async load(): Promise<void> {
    try {
      this.dashboard.set(await firstValueFrom(this.api.getDashboard()));
      this.failed.set(false);
    } catch {
      // The tiles say they could not load rather than showing stale zeros.
      this.failed.set(true);
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
