import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { Router, RouterLink } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, KioskStatus } from '../../core/api/admin-api.service';
import { KioskDetail, KioskDeviceHistoryItem, PricingProfile } from '../../core/api/step2.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { HubEvents } from '../../core/realtime/hub-events';
import { KioskLiveStore } from '../../core/realtime/kiosk-live.store';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { supportedLanguages } from '../../core/i18n/translations';
import { confirmAction } from '../../shared/confirm.dialog';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { KioskMapComponent } from '../../shared/kiosk-map.component';
import { NotifyService } from '../../shared/notify.service';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { PrintJobsComponent } from '../print-jobs/print-jobs.component';
import { KioskPrintersComponent } from './kiosk-printers.component';
import { KioskQueueComponent } from './kiosk-queue.component';
import { KioskSuppliesComponent } from './kiosk-supplies.component';

/** The PIN rules the server enforces: 4 to 12 digits. */
const pinPattern = /^[0-9]{4,12}$/;

/**
 * "Kiosk yarat/redaktə" (plan section 8A.3).
 *
 * A new kiosk is one form, created without a device (NotActivated). An
 * existing one is shown in tabs, one per part of the form, because each part
 * has its own permission (plan section 8A.2) - a tab the administrator may not
 * change is shown read-only. Printers, the queue, orders and paper each have a
 * tab of their own, shown only with the permission to view them.
 */
@Component({
  selector: 'app-kiosk-editor',
  imports: [
    NgTemplateOutlet,
    FormsModule,
    RouterLink,
    MatButtonModule,
    MatButtonToggleModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MatTableModule,
    MatTabsModule,
    TranslatePipe,
    PageHeaderComponent,
    ErrorBannerComponent,
    EmptyStateComponent,
    StatusChipComponent,
    KioskMapComponent,
    DateTimePipe,
    KioskPrintersComponent,
    KioskQueueComponent,
    KioskSuppliesComponent,
    PrintJobsComponent,
  ],
  templateUrl: './kiosk-editor.component.html',
  styleUrl: './kiosk-editor.component.scss',
})
export class KioskEditorComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly auth = inject(AuthService);
  private readonly store = inject(KioskLiveStore);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly translate = inject(TranslateService);

  /** From the route: absent on /kiosks/new. */
  readonly id = input<string | undefined>(undefined);

  readonly languages = supportedLanguages;

  /** The kiosk as this page fetched it. */
  private readonly loaded = signal<KioskDetail | null>(null);

  /**
   * The kiosk as it is shown: what was fetched, in the status the store knows
   * it to be in now. The status is not this page's to keep (T-254) - the
   * list, the dashboard and this header say the same at the same moment.
   */
  readonly kiosk = computed(() => {
    const kiosk = this.loaded();
    const status = this.store.statusOf(kiosk?.id);

    return kiosk && status && status !== kiosk.status ? { ...kiosk, status } : kiosk;
  });
  readonly devices = signal<KioskDeviceHistoryItem[]>([]);
  readonly profiles = signal<PricingProfile[]>([]);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);
  readonly saving = signal<string | null>(null);

  // General
  readonly name = signal('');
  readonly code = signal('');
  readonly lat = signal<number | null>(null);
  readonly lng = signal<number | null>(null);
  readonly address = signal('');
  readonly language = signal('az');
  readonly queueLimit = signal<number | null>(null);

  // Limits: null is "Default istifadə et"
  readonly maxSizeMb = signal<number | null>(null);
  readonly maxPages = signal<number | null>(null);

  // Pricing and fiscal
  readonly pricingProfileId = signal<string | null>(null);
  readonly fiscalTerminalId = signal('');

  // Paper
  readonly capacityA4 = signal(500);
  readonly capacityA3 = signal(0);

  // Admin PIN
  readonly pin = signal('');
  readonly pinConfirm = signal('');

  readonly isNew = computed(() => !this.id());

  readonly canEdit = computed(() => this.auth.canAny([Permissions.kiosksEdit]));
  readonly canLimits = computed(() => this.auth.canAny([Permissions.kioskLimitsManage]));
  readonly canPricing = computed(() => this.auth.canAny([Permissions.pricingManage]));
  readonly canFiscal = computed(() => this.auth.canAny([Permissions.fiscalManage]));
  readonly canDevices = computed(() => this.auth.canAny([Permissions.devicesView]));
  readonly canPrinters = computed(() => this.auth.canAny([Permissions.printersView]));
  readonly canJobs = computed(() => this.auth.canAny([Permissions.jobsView]));
  readonly canSupplies = computed(() => this.auth.canAny([Permissions.suppliesView]));
  readonly canDelete = computed(
    () =>
      this.auth.canAny([Permissions.kiosksDelete]) &&
      this.kiosk()?.status === 'NotActivated' &&
      !this.kiosk()?.activeDeviceId &&
      this.devices().length === 0,
  );

  readonly picked = computed(() => {
    const lat = this.lat();
    const lng = this.lng();

    return lat === null || lng === null ? null : { lat, lng };
  });

  // ---------------------------------------------------------------------
  // What the server would refuse, said before it is asked
  // ---------------------------------------------------------------------

  readonly generalProblems = computed(() => {
    const found: string[] = [];

    if (!this.name().trim()) {
      found.push('kiosks.invalid.name');
    }

    if (!this.address().trim()) {
      found.push('kiosks.invalid.address');
    }

    const lat = this.lat();
    const lng = this.lng();

    if (lat === null || lng === null || lat < -90 || lat > 90 || lng < -180 || lng > 180) {
      found.push('kiosks.invalid.location');
    }

    if (this.isNew() && this.code().trim() && !/^KSK-[0-9]{4}$/.test(this.code().trim())) {
      found.push('kiosks.invalid.code');
    }

    return found;
  });

  readonly limitsValid = computed(() => {
    const size = this.maxSizeMb();
    const pages = this.maxPages();

    return (
      (size === null || (Number.isInteger(size) && size >= 1 && size <= 2048)) &&
      (pages === null || (Number.isInteger(pages) && pages >= 1 && pages <= 10_000))
    );
  });

  readonly paperValid = computed(() =>
    [this.capacityA4(), this.capacityA3()].every(
      (value) => Number.isInteger(value) && value >= 0 && value <= 100_000,
    ),
  );

  readonly pinProblem = computed(() => {
    if (!this.pin()) {
      return null;
    }

    if (!pinPattern.test(this.pin())) {
      return 'kiosks.invalid.pin';
    }

    return this.pin() === this.pinConfirm() ? null : 'kiosks.invalid.pinMismatch';
  });

  readonly canCreate = computed(
    () =>
      this.generalProblems().length === 0 &&
      this.limitsValid() &&
      this.paperValid() &&
      this.pin() !== '' &&
      this.pinProblem() === null &&
      this.saving() === null,
  );

  constructor() {
    const mine = (payload: unknown): boolean =>
      (payload as { kioskId?: string } | null)?.kioskId === this.id();

    // The status in the header follows the kiosk - offline, back, paused: it
    // is the store's (T-248, T-254). What else the kiosk changes by itself is
    // fetched when its status moves.
    liveRefresh({
      events: [HubEvents.kioskStatusChanged],
      accepts: (_event, payload) => mine(payload),
      reload: () => this.refreshState(),
    });
  }

  /** After the route has set the id input. */
  ngOnInit(): void {
    void this.load();
  }

  /**
   * Fetches what the kiosk changes by itself - its status, safe mode, its last
   * heartbeat, its paper - and nothing else: the fields on this page may hold
   * edits that are not saved yet, and a full reload would throw them away.
   */
  private async refreshState(): Promise<void> {
    const id = this.id();

    if (!id || !this.kiosk()) {
      return;
    }

    try {
      const fresh = await firstValueFrom(this.api.getKiosk(id));

      this.store.learn(fresh.id, fresh.status as KioskStatus);
      this.loaded.update(
        (kiosk) =>
          kiosk && {
            ...kiosk,
            status: fresh.status,
            safeModeSince: fresh.safeModeSince,
            lastHeartbeatAt: fresh.lastHeartbeatAt,
            paper: fresh.paper,
          },
      );
    } catch {
      // What is on screen stays; the header's indicator says the panel is cut off.
    }
  }

  /** A tab learned that the kiosk's status changed (a printer was chosen, a test print passed). */
  onStatus(status: string): void {
    const id = this.id();

    if (id) {
      this.store.learn(id, status as KioskStatus);
    }
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.problem.set(null);

    try {
      if (this.canPricing()) {
        this.profiles.set(await firstValueFrom(this.api.listPricingProfiles()));
      }

      const id = this.id();

      if (id) {
        this.apply(await firstValueFrom(this.api.getKiosk(id)));

        if (this.canDevices()) {
          this.devices.set(await firstValueFrom(this.api.kioskDevices(id)));
        }
      }
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }

  onPick(point: { lat: number; lng: number }): void {
    this.lat.set(point.lat);
    this.lng.set(point.lng);
  }

  numberOrNull(value: string | number | null): number | null {
    return value === null || value === '' ? null : Number(value);
  }

  // ---------------------------------------------------------------------
  // Saving
  // ---------------------------------------------------------------------

  async create(): Promise<void> {
    if (!this.canCreate()) {
      return;
    }

    await this.run('create', async () => {
      const created = await firstValueFrom(
        this.api.createKiosk({
          name: this.name().trim(),
          code: this.code().trim() || null,
          lat: this.lat()!,
          lng: this.lng()!,
          address: this.address().trim(),
          defaultLanguage: this.language(),
          queueDisplayLimit: this.queueLimit(),
          maxFileSizeMb: this.maxSizeMb(),
          maxPages: this.maxPages(),
          pricingProfileId: this.pricingProfileId(),
          fiscalTerminalId: this.fiscalTerminalId().trim() || null,
          paperCapacityA4: this.capacityA4(),
          paperCapacityA3: this.capacityA3(),
          adminPin: this.pin(),
        }),
      );

      this.notify.success('kiosks.created', { code: created.code });
      await this.router.navigate(['/kiosks', created.id]);
    });
  }

  async saveGeneral(): Promise<void> {
    await this.run('general', async () => {
      this.apply(
        await firstValueFrom(
          this.api.updateKiosk(this.id()!, {
            name: this.name().trim(),
            lat: this.lat()!,
            lng: this.lng()!,
            address: this.address().trim(),
            defaultLanguage: this.language(),
            queueDisplayLimit: this.queueLimit(),
          }),
        ),
      );
    });
  }

  async saveLimits(): Promise<void> {
    await this.run('limits', async () => {
      this.apply(
        await firstValueFrom(
          this.api.setKioskLimits(this.id()!, this.maxSizeMb(), this.maxPages()),
        ),
      );
    });
  }

  async savePricing(): Promise<void> {
    await this.run('pricing', async () => {
      this.apply(
        await firstValueFrom(this.api.setKioskPricingProfile(this.id()!, this.pricingProfileId())),
      );
    });
  }

  async saveFiscal(): Promise<void> {
    await this.run('fiscal', async () => {
      this.apply(
        await firstValueFrom(
          this.api.setKioskFiscalTerminal(this.id()!, this.fiscalTerminalId().trim() || null),
        ),
      );
    });
  }

  async savePaper(): Promise<void> {
    await this.run('paper', async () => {
      this.apply(
        await firstValueFrom(
          this.api.setKioskPaperCapacity(this.id()!, this.capacityA4(), this.capacityA3()),
        ),
      );
    });
  }

  async savePin(): Promise<void> {
    if (!this.pin() || this.pinProblem()) {
      return;
    }

    await this.run('pin', async () => {
      this.apply(await firstValueFrom(this.api.setKioskAdminPin(this.id()!, this.pin())));
      this.pin.set('');
      this.pinConfirm.set('');
    });
  }

  async remove(): Promise<void> {
    const kiosk = this.kiosk();

    if (!kiosk) {
      return;
    }

    const confirmed = await confirmAction(this.dialog, {
      title: this.t('kiosks.deleteTitle', { code: kiosk.code }),
      explanation: this.t('kiosks.deleteExplanation'),
      confirmLabel: this.t('common.delete'),
      cancelLabel: this.t('common.cancel'),
      destructive: true,
      reasonLabel: this.t('common.reason'),
    });

    if (!confirmed) {
      return;
    }

    try {
      await firstValueFrom(this.api.deleteKiosk(kiosk.id, confirmed.reason));
      this.notify.success('kiosks.deleted', { code: kiosk.code });
      await this.router.navigate(['/kiosks']);
    } catch (error) {
      this.notify.error(toProblem(error).message);
    }
  }

  // ---------------------------------------------------------------------
  // Helpers
  // ---------------------------------------------------------------------

  private async run(part: string, work: () => Promise<void>): Promise<void> {
    this.saving.set(part);

    try {
      await work();

      if (part !== 'create') {
        this.notify.success('kiosks.saved');
      }
    } catch (error) {
      this.notify.error(toProblem(error).message);
    } finally {
      this.saving.set(null);
    }
  }

  private apply(kiosk: KioskDetail): void {
    this.loaded.set(kiosk);
    this.store.learn(kiosk.id, kiosk.status as KioskStatus);
    this.name.set(kiosk.name);
    this.lat.set(kiosk.lat);
    this.lng.set(kiosk.lng);
    this.address.set(kiosk.address);
    this.language.set(kiosk.defaultLanguage);
    this.queueLimit.set(kiosk.queueDisplayLimit ?? null);
    this.maxSizeMb.set(kiosk.maxFileSizeMb.override ?? null);
    this.maxPages.set(kiosk.maxPages.override ?? null);
    this.pricingProfileId.set(kiosk.pricingProfileId ?? null);
    this.fiscalTerminalId.set(kiosk.fiscalTerminalId ?? '');
    this.capacityA4.set(kiosk.paper['A4']?.capacity ?? 0);
    this.capacityA3.set(kiosk.paper['A3']?.capacity ?? 0);
  }

  private t(key: string, params?: Record<string, unknown>): string {
    return this.translate.instant(key, params) as string;
  }
}
