import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService } from '../../core/api/admin-api.service';
import {
  StorageHealthChange,
  StorageProvider,
  StorageSyncJob,
  StorageSyncOperation,
  StorageSyncProgress,
  activeSyncStatuses,
} from '../../core/api/step2.models';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { confirmAction } from '../../shared/confirm.dialog';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { BytesPipe, CountPipe, DateTimePipe, formatDateTime } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { StorageFailuresDialogComponent } from './storage-failures.dialog';
import {
  StorageProviderDialogComponent,
  StorageProviderDialogResult,
} from './storage-provider.dialog';
import { StorageSyncDialogComponent, StorageSyncDialogData } from './storage-sync.dialog';
import { SyncProgressComponent } from './sync-progress.component';
import { HubEvents } from '../../core/realtime/hub-events';

/** One progress reading, for working out a speed. */
interface Sample {
  readonly at: number;
  readonly bytes: number;
}

/**
 * The "Fayl saxlama" page (plan sections 2.10, 8A.3, 9.8): the S3-compatible
 * providers and their roles, and the sync between them - migration or mirror -
 * with live progress over the admin hub.
 *
 * Everything here needs storage.manage, which only SuperAdmin holds by
 * default: pointing the installation at another store is the most
 * consequential thing the panel can do.
 */
@Component({
  selector: 'app-storage',
  imports: [
    MatButtonModule,
    MatIconModule,
    MatMenuModule,
    MatTableModule,
    MatTabsModule,
    MatTooltipModule,
    TranslatePipe,
    PageHeaderComponent,
    ErrorBannerComponent,
    EmptyStateComponent,
    StatusChipComponent,
    SyncProgressComponent,
    BytesPipe,
    CountPipe,
    DateTimePipe,
  ],
  templateUrl: './storage.component.html',
  styleUrl: './storage.component.scss',
})
export class StorageComponent {
  private readonly api = inject(AdminApiService);
  private readonly hub = inject(AdminHubService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly translate = inject(TranslateService);

  readonly providers = signal<StorageProvider[]>([]);
  readonly jobs = signal<StorageSyncJob[]>([]);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);
  readonly busy = signal(false);
  readonly tab = signal(0);

  private readonly samples = signal<Sample[]>([]);

  readonly providerColumns = ['name', 'endpoint', 'role', 'health', 'checked', 'actions'];
  readonly jobColumns = [
    'mode',
    'route',
    'status',
    'objects',
    'size',
    'started',
    'finished',
    'actions',
  ];

  readonly activeJob = computed(
    () => this.jobs().find((job) => activeSyncStatuses.includes(job.status)) ?? null,
  );

  /** Bytes per second over the last few readings of the active job. */
  readonly speed = computed(() => {
    const samples = this.samples();

    if (samples.length < 2) {
      return null;
    }

    const first = samples[0];
    const last = samples[samples.length - 1];
    const seconds = (last.at - first.at) / 1000;

    return seconds > 0 ? Math.max(0, (last.bytes - first.bytes) / seconds) : null;
  });

  constructor() {
    const off = [
      this.hub.on<StorageSyncProgress>(HubEvents.storageSyncProgress, (progress) =>
        this.onProgress(progress),
      ),
      this.hub.on<StorageSyncProgress>(HubEvents.storageSyncCompleted, () => void this.load()),
      this.hub.on<StorageHealthChange>(HubEvents.storageHealthChanged, (change) => this.onHealth(change)),
    ];

    inject(DestroyRef).onDestroy(() => off.forEach((unsubscribe) => unsubscribe()));

    // A sync's progress and a provider's health are events; after a gap in
    // the connection they are asked for again.
    liveRefresh({ events: [], reload: () => this.load() });

    void this.load();
  }

  async load(): Promise<void> {
    this.problem.set(null);

    try {
      const [providers, jobs] = await Promise.all([
        firstValueFrom(this.api.listStorageProviders()),
        firstValueFrom(this.api.listStorageSyncJobs()),
      ]);

      this.providers.set(providers);
      this.jobs.set(jobs);
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }

  // ---------------------------------------------------------------------
  // Providers
  // ---------------------------------------------------------------------

  async addProvider(): Promise<void> {
    await this.openProvider(null);
  }

  async editProvider(provider: StorageProvider): Promise<void> {
    await this.openProvider(provider);
  }

  async testProvider(provider: StorageProvider): Promise<void> {
    this.busy.set(true);

    try {
      const result = await firstValueFrom(this.api.testStorageProvider(provider.id));

      if (result.succeeded) {
        this.notify.success('storage.provider.testPassed', { name: provider.name });
      } else {
        this.notify.error(result.message);
      }

      await this.load();
    } catch (error) {
      this.notify.error(toProblem(error).message);
    } finally {
      this.busy.set(false);
    }
  }

  async makePrimary(provider: StorageProvider): Promise<void> {
    const confirmed = await confirmAction(this.dialog, {
      title: this.t('storage.provider.makePrimaryTitle', { name: provider.name }),
      explanation: this.t('storage.provider.makePrimaryExplanation'),
      confirmLabel: this.t('storage.provider.makePrimary'),
      cancelLabel: this.t('common.cancel'),
    });

    if (confirmed) {
      await this.setRole(provider, 'Primary');
    }
  }

  /** A mirror starts with a copy, so "Secondary" goes through the wizard. */
  async makeSecondary(provider: StorageProvider): Promise<void> {
    await this.startSync({ providers: this.providers(), targetId: provider.id, mode: 'Mirror' });
  }

  async deactivate(provider: StorageProvider): Promise<void> {
    const confirmed = await confirmAction(this.dialog, {
      title: this.t('storage.provider.deactivateTitle', { name: provider.name }),
      explanation: this.t('storage.provider.deactivateExplanation'),
      confirmLabel: this.t('storage.provider.deactivate'),
      cancelLabel: this.t('common.cancel'),
      destructive: true,
      reasonLabel: this.t('common.reason'),
    });

    if (confirmed) {
      await this.setRole(provider, 'Inactive', confirmed.reason);
    }
  }

  async deleteProvider(provider: StorageProvider): Promise<void> {
    const confirmed = await confirmAction(this.dialog, {
      title: this.t('storage.provider.deleteTitle', { name: provider.name }),
      explanation: this.t('storage.provider.deleteExplanation'),
      confirmLabel: this.t('common.delete'),
      cancelLabel: this.t('common.cancel'),
      destructive: true,
      reasonLabel: this.t('common.reason'),
    });

    if (!confirmed) {
      return;
    }

    try {
      await firstValueFrom(this.api.deleteStorageProvider(provider.id, confirmed.reason));
      this.notify.success('storage.provider.deleted', { name: provider.name });
      await this.load();
    } catch (error) {
      this.notify.error(toProblem(error).message);
    }
  }

  async rewrap(): Promise<void> {
    const confirmed = await confirmAction(this.dialog, {
      title: this.t('storage.rewrapTitle'),
      explanation: this.t('storage.rewrapExplanation'),
      confirmLabel: this.t('storage.rewrap'),
      cancelLabel: this.t('common.cancel'),
    });

    if (!confirmed) {
      return;
    }

    try {
      await firstValueFrom(this.api.startKeyRewrap());
      this.notify.success('storage.rewrapStarted');
    } catch (error) {
      this.notify.error(toProblem(error).message);
    }
  }

  // ---------------------------------------------------------------------
  // Sync
  // ---------------------------------------------------------------------

  async newSync(): Promise<void> {
    await this.startSync({ providers: this.providers() });
  }

  async control(job: StorageSyncJob, operation: StorageSyncOperation): Promise<void> {
    let reason: string | undefined;

    if (operation === 'cancel') {
      const confirmed = await confirmAction(this.dialog, {
        title: this.t('storage.progress.cancelTitle'),
        explanation: this.t(
          job.mode === 'Migration'
            ? 'storage.progress.cancelMigration'
            : 'storage.progress.cancelMirror',
        ),
        confirmLabel: this.t('storage.progress.cancel'),
        cancelLabel: this.t('common.back'),
        destructive: true,
        reasonLabel: this.t('common.reason'),
      });

      if (!confirmed) {
        return;
      }

      reason = confirmed.reason;
    }

    this.busy.set(true);

    try {
      const updated = await firstValueFrom(this.api.controlStorageSync(job.id, operation, reason));
      this.replaceJob(updated);
      this.notify.success(`storage.progress.done.${operation}`);
    } catch (error) {
      this.notify.error(toProblem(error).message);
    } finally {
      this.busy.set(false);
    }
  }

  showFailures(job: StorageSyncJob): void {
    this.dialog.open(StorageFailuresDialogComponent, { data: job, width: 'min(960px, 95vw)' });
  }

  /** Whether the source of this job can be emptied now. */
  canPurge(job: StorageSyncJob): boolean {
    return (
      job.mode === 'Migration' &&
      job.status === 'Completed' &&
      !job.sourcePurgedAt &&
      !!job.sourcePurgeAllowedFrom &&
      new Date(job.sourcePurgeAllowedFrom).getTime() <= Date.now()
    );
  }

  purgeState(job: StorageSyncJob): 'none' | 'waiting' | 'ready' | 'running' | 'done' {
    if (job.mode !== 'Migration' || job.status !== 'Completed') {
      return 'none';
    }

    if (job.sourcePurgedAt) {
      return 'done';
    }

    if (job.sourcePurgeRequestedAt) {
      return 'running';
    }

    return this.canPurge(job) ? 'ready' : 'waiting';
  }

  purgeDate(job: StorageSyncJob): string {
    return formatDateTime(job.sourcePurgeAllowedFrom);
  }

  async purge(job: StorageSyncJob): Promise<void> {
    const confirmed = await confirmAction(this.dialog, {
      title: this.t('storage.purge.title', { name: job.sourceName }),
      explanation: this.t('storage.purge.explanation', {
        source: job.sourceName,
        target: job.targetName,
      }),
      confirmLabel: this.t('storage.purge.confirm'),
      cancelLabel: this.t('common.cancel'),
      destructive: true,
      reasonLabel: this.t('common.reason'),
    });

    if (!confirmed) {
      return;
    }

    try {
      this.replaceJob(await firstValueFrom(this.api.purgeStorageSource(job.id, confirmed.reason)));
      this.notify.success('storage.purge.started');
    } catch (error) {
      this.notify.error(toProblem(error).message);
    }
  }

  // ---------------------------------------------------------------------
  // Helpers
  // ---------------------------------------------------------------------

  private async openProvider(existing: StorageProvider | null): Promise<void> {
    const saved = await firstValueFrom(
      this.dialog
        .open<
          StorageProviderDialogComponent,
          StorageProvider | null,
          StorageProviderDialogResult
        >(StorageProviderDialogComponent, { data: existing, width: 'min(720px, 95vw)', autoFocus: 'first-tabbable' })
        .afterClosed(),
    );

    if (saved) {
      this.notify.success(existing ? 'storage.provider.updated' : 'storage.provider.added', {
        name: saved.name,
      });
      await this.load();
    }
  }

  private async startSync(data: StorageSyncDialogData): Promise<void> {
    const job = await firstValueFrom(
      this.dialog
        .open<
          StorageSyncDialogComponent,
          StorageSyncDialogData,
          StorageSyncJob | undefined
        >(StorageSyncDialogComponent, { data, width: 'min(640px, 95vw)' })
        .afterClosed(),
    );

    if (job) {
      this.samples.set([]);
      this.tab.set(1);
      this.notify.success('storage.sync.started');
      await this.load();
    }
  }

  private async setRole(
    provider: StorageProvider,
    role: 'Primary' | 'Inactive',
    reason?: string,
  ): Promise<void> {
    try {
      await firstValueFrom(this.api.setStorageRole(provider.id, role, reason));
      this.notify.success(`storage.provider.role${role}`, { name: provider.name });
      await this.load();
    } catch (error) {
      this.notify.error(toProblem(error).message);
    }
  }

  private onProgress(progress: StorageSyncProgress): void {
    const known = this.jobs().some((job) => job.id === progress.jobId);

    if (!known) {
      void this.load();
      return;
    }

    this.jobs.update((jobs) =>
      jobs.map((job) =>
        job.id === progress.jobId
          ? {
              ...job,
              status: progress.status,
              totalObjects: progress.totalObjects,
              copiedObjects: progress.copiedObjects,
              failedObjects: progress.failedObjects,
              totalBytes: progress.totalBytes,
              copiedBytes: progress.copiedBytes,
              failureReason: progress.failureReason,
            }
          : job,
      ),
    );

    // The last half-minute of readings: long enough to smooth one slow
    // object out, short enough to follow a real change of pace.
    const now = new Date(progress.at).getTime();
    this.samples.update((samples) =>
      [...samples, { at: now, bytes: progress.copiedBytes }].filter(
        (sample) => now - sample.at <= 30_000,
      ),
    );

    if (!activeSyncStatuses.includes(progress.status)) {
      void this.load();
    }
  }

  private onHealth(change: StorageHealthChange): void {
    this.providers.update((providers) =>
      providers.map((provider) =>
        provider.id === change.providerId
          ? {
              ...provider,
              healthStatus: change.status,
              healthMessage: change.message,
              lastCheckedAt: change.at,
            }
          : provider,
      ),
    );

    if (change.status === 'Unhealthy') {
      this.notify.error('storage.provider.becameUnhealthy', { name: change.name });
    }
  }

  private replaceJob(updated: StorageSyncJob): void {
    this.jobs.update((jobs) => jobs.map((job) => (job.id === updated.id ? updated : job)));
  }

  private t(key: string, params?: Record<string, unknown>): string {
    return this.translate.instant(key, params) as string;
  }
}
