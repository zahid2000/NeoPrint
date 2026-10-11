import { MoneyCardComponent } from '../money/money-card.component';
import { Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, KioskListItem, KioskStatus } from '../../core/api/admin-api.service';
import { AdminDashboard, StorageSyncJob } from '../../core/api/step2.models';
import { lowPaperShare } from '../../core/api/step3.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { HasPermissionDirective } from '../../core/auth/has-permission.directive';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { BytesPipe, CountPipe, DateTimePipe } from '../../shared/format.pipes';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { StatusChipComponent, StatusTone } from '../../shared/status-chip.component';

/** Why a kiosk needs looking at, most urgent first. */
export type AttentionReason =
  | 'safeMode'
  | 'offline'
  | 'notConfigured'
  | 'noPaper'
  | 'maintenance'
  | 'paused'
  | 'lowPaper';

export interface KioskAttention {
  readonly kiosk: KioskListItem;
  readonly reason: AttentionReason;
  readonly tone: StatusTone;
  readonly params: Record<string, unknown>;
}

/**
 * A kiosk whose last heartbeat is older than this has lost its connection,
 * whatever its stored status says: the monitor that turns the status Offline
 * comes with step 7 (plan section 7, KioskHeartbeatMonitor; docs/TASKS.md T-083).
 */
export const staleHeartbeatMs = 5 * 60 * 1000;

const isSilent = (kiosk: KioskListItem, now: number): boolean =>
  !!kiosk.lastHeartbeatAt && now - new Date(kiosk.lastHeartbeatAt).getTime() > staleHeartbeatMs;

/**
 * The status the dashboard counts a kiosk under: its stored status, except
 * that a silent kiosk is Offline - so the counts and the attention list below
 * them tell the same story (docs/TASKS.md T-091). A disabled or not yet
 * activated kiosk is not expected to send anything.
 */
export function shownStatus(kiosk: KioskListItem, now = Date.now()): KioskStatus {
  if (kiosk.status === 'Disabled' || kiosk.status === 'NotActivated') {
    return kiosk.status;
  }

  return isSilent(kiosk, now) ? 'Offline' : kiosk.status;
}

/**
 * The one reason a kiosk is listed for, or null when it needs nothing. A
 * disabled or not yet activated kiosk is not expected to serve anyone.
 */
export function attentionFor(kiosk: KioskListItem, now = Date.now()): KioskAttention | null {
  const make = (
    reason: AttentionReason,
    tone: StatusTone,
    params: Record<string, unknown> = {},
  ) => ({
    kiosk,
    reason,
    tone,
    params,
  });

  if (kiosk.status === 'Disabled' || kiosk.status === 'NotActivated') {
    return null;
  }

  if (isSilent(kiosk, now)) {
    return make('offline', 'danger');
  }

  if (kiosk.safeModeSince) {
    return make('safeMode', 'danger');
  }

  switch (kiosk.status) {
    case 'Offline':
      return make('offline', 'danger');
    case 'NotConfigured':
      return make('notConfigured', 'warning');
  }

  const capacity = kiosk.paperA4Capacity ?? 0;
  const current = kiosk.paperA4Current ?? 0;

  if (capacity > 0 && current === 0) {
    return make('noPaper', 'danger');
  }

  switch (kiosk.status) {
    case 'Maintenance':
      return make('maintenance', 'warning');
    case 'Paused':
      return make('paused', 'warning');
  }

  if (capacity > 0 && current < capacity * lowPaperShare) {
    return make('lowPaper', 'warning', { current });
  }

  return null;
}

/**
 * The kiosk list's status order. Kept here rather than imported from the
 * kiosks page so the dashboard does not pull that page (and its map) in.
 */
const kioskStatuses: readonly KioskStatus[] = [
  'Online',
  'Paused',
  'Offline',
  'Maintenance',
  'NotConfigured',
  'NotActivated',
  'Disabled',
];

const attentionOrder: readonly AttentionReason[] = [
  'safeMode',
  'offline',
  'notConfigured',
  'noPaper',
  'maintenance',
  'paused',
  'lowPaper',
];

/**
 * The dashboard (plan section 8A.3).
 *
 * It fills up step by step as the features it reports on arrive: step 1 the
 * devices waiting for activation and the kiosk scope, step 2 files,
 * conversion failures, storage health and sync, step 3 the kiosk statuses and
 * the kiosks that need looking at. What an account may do is its roles' business
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
    PageHeaderComponent,
    StatusChipComponent,
    MoneyCardComponent,
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

  readonly canKiosks = computed(() => this.auth.can(Permissions.kiosksView));
  readonly canMoney = computed(() => this.auth.can(Permissions.paymentsView));
  readonly kiosks = signal<KioskListItem[] | null>(null);
  readonly kiosksFailed = signal(false);

  /** How many kiosks are in each status, in the order the kiosk list uses; empty statuses left out. */
  readonly kioskCounts = computed(() => {
    const list = this.kiosks() ?? [];

    return kioskStatuses
      .map((status: KioskStatus) => ({
        status,
        count: list.filter((kiosk) => shownStatus(kiosk) === status).length,
      }))
      .filter((entry) => entry.count > 0);
  });

  readonly attention = computed(() =>
    (this.kiosks() ?? [])
      .map((kiosk) => attentionFor(kiosk))
      .filter((item): item is KioskAttention => item !== null)
      .sort((a, b) => attentionOrder.indexOf(a.reason) - attentionOrder.indexOf(b.reason)),
  );

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

    if (this.canKiosks()) {
      void this.loadKiosks();
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

  private async loadKiosks(): Promise<void> {
    try {
      this.kiosks.set(await firstValueFrom(this.api.listKiosks()));
      this.kiosksFailed.set(false);
    } catch {
      this.kiosksFailed.set(true);
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
