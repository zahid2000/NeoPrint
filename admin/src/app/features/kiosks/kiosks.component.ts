import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { Router } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';

import { KioskListItem, KioskStatus } from '../../core/api/admin-api.service';
import { Permissions } from '../../core/auth/auth.models';
import { HasPermissionDirective } from '../../core/auth/has-permission.directive';
import { KioskLiveStore } from '../../core/realtime/kiosk-live.store';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { FilterBarComponent } from '../../shared/filter-bar.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { KioskMapComponent, MapMarker } from '../../shared/kiosk-map.component';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { StatusChipComponent, StatusTone } from '../../shared/status-chip.component';

export const kioskStatuses: readonly KioskStatus[] = [
  'Online',
  'Paused',
  'Offline',
  'Maintenance',
  'NotConfigured',
  'NotActivated',
  'Disabled',
];

/** The same tones the status chip uses, for the map's markers and legend. */
export const kioskTone: Record<KioskStatus, StatusTone> = {
  Online: 'success',
  Paused: 'warning',
  Maintenance: 'warning',
  Offline: 'danger',
  Disabled: 'danger',
  NotConfigured: 'neutral',
  NotActivated: 'neutral',
};

/**
 * The "Kiosklar" page (plan section 8A.3): the list, or the same kiosks on a
 * map, filtered by status and a search. A row or a marker opens the kiosk.
 *
 * The kiosks and their statuses are the store's (T-254): this page keeps no
 * copy, so a kiosk that went offline shows here at the moment it shows on the
 * dashboard, and neither needs a reload (T-248).
 */
@Component({
  selector: 'app-kiosks',
  imports: [
    FormsModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    MatTableModule,
    TranslatePipe,
    HasPermissionDirective,
    PageHeaderComponent,
    FilterBarComponent,
    ErrorBannerComponent,
    EmptyStateComponent,
    StatusChipComponent,
    KioskMapComponent,
    DateTimePipe,
  ],
  templateUrl: './kiosks.component.html',
  styleUrl: './kiosks.component.scss',
})
export class KiosksComponent {
  private readonly store = inject(KioskLiveStore);
  private readonly router = inject(Router);
  private readonly translate = inject(TranslateService);

  readonly permissions = Permissions;
  readonly statuses = kioskStatuses;
  readonly tone = kioskTone;

  readonly kiosks = computed(() => this.store.kiosks() ?? []);
  readonly loading = this.store.loading;
  readonly problem = this.store.problem;

  readonly search = signal('');
  readonly status = signal<KioskStatus | 'all'>('all');
  readonly view = signal<'list' | 'map'>('list');

  readonly columns = ['code', 'name', 'status', 'address', 'version', 'heartbeat'];

  /** A kiosk that serves with an A4 tray nobody has counted (T-264). One with no device serves nobody. */
  counterUnset(kiosk: KioskListItem): boolean {
    return (
      (kiosk.paperA4Capacity ?? 0) > 0 &&
      kiosk.paperA4Counted === false &&
      kiosk.status !== 'NotActivated' &&
      kiosk.status !== 'Disabled'
    );
  }

  readonly filtered = computed(() => {
    const term = this.search().trim().toLocaleLowerCase();
    const status = this.status();

    return this.kiosks().filter(
      (kiosk) =>
        (status === 'all' || kiosk.status === status) &&
        (term === '' ||
          kiosk.code.toLocaleLowerCase().includes(term) ||
          kiosk.name.toLocaleLowerCase().includes(term) ||
          kiosk.address.toLocaleLowerCase().includes(term)),
    );
  });

  readonly filtersActive = computed(() => this.search().trim() !== '' || this.status() !== 'all');

  /** Kiosks with a real position; one at 0,0 has not been placed yet. */
  readonly markers = computed<MapMarker[]>(() =>
    this.filtered()
      .filter(
        (kiosk) =>
          kiosk.lat !== undefined &&
          kiosk.lng !== undefined &&
          (kiosk.lat !== 0 || kiosk.lng !== 0),
      )
      .map((kiosk) => ({
        id: kiosk.id,
        lat: kiosk.lat!,
        lng: kiosk.lng!,
        title: `${kiosk.code} — ${kiosk.name}`,
        subtitle: this.translate.instant(`kiosks.status.${kiosk.status}`) as string,
        tone: kioskTone[kiosk.status],
      })),
  );

  readonly unplaced = computed(() => this.filtered().length - this.markers().length);

  constructor() {
    this.store.watch();
  }

  /** "Yenidən cəhd et" on the error banner. */
  load(): void {
    void this.store.load();
  }

  open(id: string): void {
    void this.router.navigate(['/kiosks', id]);
  }

  create(): void {
    void this.router.navigate(['/kiosks', 'new']);
  }

  clearFilters(): void {
    this.search.set('');
    this.status.set('all');
  }
}
