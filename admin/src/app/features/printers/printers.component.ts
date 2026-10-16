import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MonitoringApiService } from '../../core/api/monitoring-api.service';
import { PrinterReading } from '../../core/api/step7.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { HubEvents } from '../../core/realtime/hub-events';
import { KioskLiveStore } from '../../core/realtime/kiosk-live.store';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { TelemetryLiveStore } from '../../core/realtime/telemetry-live.store';
import { SupplyAlert } from '../operator/operator.models';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { CountPipe, DateTimePipe } from '../../shared/format.pipes';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem } from '../../shared/problem';
import { StatusChipComponent, StatusTone } from '../../shared/status-chip.component';
import { PrinterStatus, lowestToner, printerStatus } from './printer-status';

type Filter = 'all' | 'lowToner' | 'faulty' | 'unreachable';

interface PrinterRow {
  readonly key: string;
  readonly kioskId: string;
  readonly kioskCode: string;
  readonly printer: string;
  readonly model: string;
  readonly selected: boolean;
  readonly status: PrinterStatus;
  readonly toner: { name: string; percent: number } | null;
  readonly tonerTone: StatusTone | null;
  readonly lifetimePages: number | null;
  readonly readAt: string;
  readonly lowToner: boolean;
}

/**
 * "Printerlər (ümumi)" (plan section 8A.3): every printer of every kiosk the
 * account sees, with its status, its lowest toner and its lifetime counter,
 * filtered to the ones that need somebody. The readings are the panel's one
 * copy ({@link TelemetryLiveStore}), live; the toner alerts are this page's.
 */
@Component({
  selector: 'app-printers',
  imports: [
    FormsModule,
    RouterLink,
    MatButtonModule,
    MatButtonToggleModule,
    MatTableModule,
    TranslatePipe,
    CountPipe,
    DateTimePipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    PageHeaderComponent,
    StatusChipComponent,
  ],
  template: `
    <div class="neo-page">
      <app-page-header [title]="'monitoring.printers.title' | translate" [description]="'monitoring.printers.description' | translate" />

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="monitoring.printers.loadFailedEffect" (retry)="load()" />
      }

      <mat-button-toggle-group
        class="printers__filter"
        [attr.aria-label]="'monitoring.filter' | translate"
        [ngModel]="filter()"
        (ngModelChange)="filter.set($event)"
      >
        @for (option of filters(); track option) {
          <mat-button-toggle [value]="option">
            {{ 'monitoring.printers.filter.' + option | translate: { count: counts()[option] } }}
          </mat-button-toggle>
        }
      </mat-button-toggle-group>

      @if (rows() === null) {
        @if (!problem()) {
          <div class="neo-skeleton neo-skeleton--block"></div>
        }
      } @else if (rows()!.length === 0) {
        <app-empty-state
          icon="print_disabled"
          [message]="'monitoring.printers.empty' | translate"
          [hint]="'monitoring.printers.emptyHint' | translate"
        />
      } @else if (shown().length === 0) {
        <app-empty-state icon="search_off" [message]="'monitoring.printers.noMatch' | translate">
          <button mat-stroked-button type="button" (click)="filter.set('all')">
            {{ 'monitoring.printers.clearFilter' | translate }}
          </button>
        </app-empty-state>
      } @else {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="shown()">
            <ng-container matColumnDef="kiosk">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.printers.colKiosk' | translate }}</th>
              <td mat-cell *matCellDef="let row" class="neo-nowrap">
                <a [routerLink]="['/kiosks', row.kioskId]">{{ row.kioskCode }}</a>
              </td>
            </ng-container>
            <ng-container matColumnDef="printer">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.printers.colPrinter' | translate }}</th>
              <td mat-cell *matCellDef="let row">
                <a [routerLink]="['/kiosks', row.kioskId, 'printers', row.printer]">{{ row.printer }}</a>
                @if (row.selected) {
                  <app-status-chip status="Selected" toneOverride="success" [text]="'printers.selected' | translate" />
                }
                @if (row.model) {
                  <span class="neo-caption printers__block">{{ row.model }}</span>
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'printers.colStatus' | translate }}</th>
              <td mat-cell *matCellDef="let row">
                <app-status-chip
                  [status]="row.status.key"
                  [toneOverride]="row.status.tone"
                  [iconOverride]="row.status.icon"
                  [text]="'monitoring.printerState.' + row.status.key | translate"
                />
              </td>
            </ng-container>
            <ng-container matColumnDef="toner">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.printers.colToner' | translate }}</th>
              <td mat-cell *matCellDef="let row">
                @if (row.toner; as toner) {
                  <span [class]="row.tonerTone ? 'printers__tone--' + row.tonerTone : ''">
                    {{ toner.name }}: {{ toner.percent }}%
                  </span>
                } @else {
                  <span class="neo-caption">{{ 'monitoring.printerNotReported' | translate }}</span>
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="pages">
              <th mat-header-cell *matHeaderCellDef class="printers__number">{{ 'monitoring.printers.colPages' | translate }}</th>
              <td mat-cell *matCellDef="let row" class="printers__number neo-nowrap">
                @if (row.lifetimePages === null) {
                  <span class="neo-caption">—</span>
                } @else {
                  {{ row.lifetimePages | count }}
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="readAt">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.printers.colReadAt' | translate }}</th>
              <td mat-cell *matCellDef="let row" class="neo-nowrap">{{ row.readAt | dateTime }}</td>
            </ng-container>

            <tr mat-header-row *matHeaderRowDef="columns"></tr>
            <tr mat-row *matRowDef="let row; columns: columns"></tr>
          </table>
        </div>
      }
    </div>
  `,
  styles: `
    .printers__filter {
      align-self: flex-start;
      flex-wrap: wrap;
    }

    .printers__block {
      display: block;
    }

    .printers__number {
      text-align: right;
      font-variant-numeric: tabular-nums;
    }

    .printers__tone--warning {
      color: var(--warning-500);
      font-weight: var(--font-weight-semibold);
    }

    .printers__tone--danger {
      color: var(--danger-500);
      font-weight: var(--font-weight-semibold);
    }
  `,
})
export class PrintersComponent {
  private readonly api = inject(MonitoringApiService);
  private readonly auth = inject(AuthService);
  private readonly store = inject(TelemetryLiveStore);
  private readonly kiosks = inject(KioskLiveStore);
  private readonly destroyRef = inject(DestroyRef);

  readonly columns = ['kiosk', 'printer', 'status', 'toner', 'pages', 'readAt'];

  readonly filter = signal<Filter>('all');
  readonly problem = signal<Problem | null>(null);

  /** The selected printers under a toner alert now, by kiosk. */
  private readonly alerts = signal<readonly SupplyAlert[]>([]);

  readonly canSeeSupplies = computed(() => this.auth.can(Permissions.suppliesView));

  readonly filters = computed<Filter[]>(() =>
    this.canSeeSupplies() ? ['all', 'lowToner', 'faulty', 'unreachable'] : ['all', 'faulty', 'unreachable'],
  );

  readonly rows = computed<PrinterRow[] | null>(() => {
    const readings = this.store.allPrinters();

    if (readings === null) {
      return null;
    }

    const codes = new Map((this.kiosks.kiosks() ?? []).map((kiosk) => [kiosk.id, kiosk.code]));
    const alerts = this.alerts();

    return readings
      .map((reading) => this.row(reading, codes.get(reading.kioskId) ?? '', alerts))
      .sort((a, b) => a.kioskCode.localeCompare(b.kioskCode) || Number(b.selected) - Number(a.selected));
  });

  readonly counts = computed<Record<Filter, number>>(() => {
    const rows = this.rows() ?? [];

    return {
      all: rows.length,
      lowToner: rows.filter((row) => row.lowToner).length,
      faulty: rows.filter((row) => this.faulty(row)).length,
      unreachable: rows.filter((row) => row.status.key === 'unreachable').length,
    };
  });

  readonly shown = computed(() => {
    const rows = this.rows() ?? [];

    switch (this.filter()) {
      case 'lowToner':
        return rows.filter((row) => row.lowToner);
      case 'faulty':
        return rows.filter((row) => this.faulty(row));
      case 'unreachable':
        return rows.filter((row) => row.status.key === 'unreachable');
      default:
        return rows;
    }
  });

  constructor() {
    this.kiosks.watch();
    void this.load();

    // The toner alerts are the server's, not a guess from the levels: the
    // rule for the kiosk decides what is low.
    liveRefresh({
      events: [HubEvents.supplyAlertRaised, HubEvents.supplyAlertCleared],
      reload: () => this.loadAlerts(),
    });
  }

  async load(): Promise<void> {
    this.problem.set(null);
    this.problem.set(await this.store.watchAllPrinters(this.destroyRef));
    await this.loadAlerts();
  }

  private async loadAlerts(): Promise<void> {
    if (!this.canSeeSupplies()) {
      return;
    }

    try {
      const page = await firstValueFrom(this.api.activeSupplyAlerts());
      this.alerts.set(page.items.filter((alert) => alert.resource === 'Toner'));
    } catch {
      // Without the alerts the list still shows; only the "Az toner" count waits for the next try.
    }
  }

  private faulty(row: PrinterRow): boolean {
    return row.status.key === 'stopped' || row.status.key === 'attention';
  }

  private row(reading: PrinterReading, kioskCode: string, alerts: readonly SupplyAlert[]): PrinterRow {
    const sample = reading.sample;
    const alert = sample.selected
      ? alerts.find((candidate) => candidate.kioskId === reading.kioskId)
      : undefined;

    return {
      key: `${reading.kioskId}:${sample.printer.printerName}`,
      kioskId: reading.kioskId,
      kioskCode,
      printer: sample.printer.printerName,
      model: [sample.manufacturer, sample.model].filter(Boolean).join(' '),
      selected: sample.selected,
      status: printerStatus(reading),
      toner: lowestToner(reading),
      tonerTone: alert ? (alert.level === 'Critical' ? 'danger' : 'warning') : null,
      lifetimePages: sample.lifetimePages ?? null,
      readAt: sample.timestamp,
      lowToner: !!alert,
    };
  }
}
