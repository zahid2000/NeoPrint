import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { IncidentView, OperationsApiService } from '../../core/api/operations-api.service';
import { IncidentRow } from '../../core/api/step4.models';
import { IncidentDetail } from '../../core/api/step7.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { HubEvents, IncidentEvent } from '../../core/realtime/hub-events';
import { IncidentLiveStore } from '../../core/realtime/incident-live.store';
import { KioskLiveStore } from '../../core/realtime/kiosk-live.store';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { confirmAction } from '../../shared/confirm.dialog';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { FilterBarComponent } from '../../shared/filter-bar.component';
import { DateTimePipe, MoneyPipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent, StatusTone } from '../../shared/status-chip.component';
import { PagerComponent } from '../money/money-shared';
import { OperatorNotifyService } from '../operator/operator-notify.service';

const pageSize = 20;

/** An incident's state in the tone the kiosk tab gives it too. */
export const incidentStatusTones: Readonly<Record<string, StatusTone>> = {
  Open: 'danger',
  Acknowledged: 'warning',
  Resolved: 'success',
};

/** The severities that are said aloud when they arrive. */
const loud = new Set(['Critical', 'High']);

/**
 * "Incident-lər" (plan sections 8A.3, 9.6): what went wrong anywhere in the
 * account's kiosks, Critical first, live - with the orders each one touched,
 * "Baxıram" and "Bağla". A new Critical or High incident is said aloud
 * (sound and a browser notification) once the administrator allows it.
 */
@Component({
  selector: 'app-incidents',
  imports: [
    FormsModule,
    RouterLink,
    MatButtonModule,
    MatButtonToggleModule,
    MatFormFieldModule,
    MatIconModule,
    MatSelectModule,
    MatTableModule,
    TranslatePipe,
    DateTimePipe,
    MoneyPipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    FilterBarComponent,
    PageHeaderComponent,
    PagerComponent,
    StatusChipComponent,
  ],
  template: `
    <div class="neo-page">
      <app-page-header [title]="'ops.incidents.title' | translate" [description]="'ops.incidents.description' | translate">
        @if (openCount() !== null) {
          <app-status-chip
            status="Open"
            [toneOverride]="openCount() === 0 ? 'success' : 'danger'"
            [text]="'ops.incidents.openCount' | translate: { count: openCount() }"
          />
        }
      </app-page-header>

      @if (notify.access() === 'default') {
        <div class="neo-notice" role="status">
          <mat-icon aria-hidden="true">info</mat-icon>
          <span>{{ 'ops.incidents.notifyAsk' | translate }}</span>
          <button mat-stroked-button type="button" (click)="notify.enable()">
            {{ 'ops.incidents.notifyEnable' | translate }}
          </button>
        </div>
      }

      <app-filter-bar>
        <mat-button-toggle-group [attr.aria-label]="'monitoring.filter' | translate" [ngModel]="view()" (ngModelChange)="setView($event)">
          <mat-button-toggle value="open">{{ 'ops.incidents.viewOpen' | translate }}</mat-button-toggle>
          <mat-button-toggle value="resolved">{{ 'ops.incidents.viewResolved' | translate }}</mat-button-toggle>
          <mat-button-toggle value="all">{{ 'monitoring.filterAll' | translate }}</mat-button-toggle>
        </mat-button-toggle-group>
        <mat-form-field>
          <mat-label>{{ 'ops.kiosk' | translate }}</mat-label>
          <mat-select [ngModel]="kioskId()" (ngModelChange)="setKiosk($event)">
            <mat-option [value]="null">{{ 'ops.allKiosks' | translate }}</mat-option>
            @for (kiosk of kiosks(); track kiosk.id) {
              <mat-option [value]="kiosk.id">{{ kiosk.code }} — {{ kiosk.name }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      </app-filter-bar>

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="ops.incidents.loadFailedEffect" (retry)="load()" />
      } @else if (rows() === null) {
        <div class="neo-skeleton neo-skeleton--block"></div>
      } @else if (rows()!.length === 0) {
        <app-empty-state
          [icon]="view() === 'open' ? 'verified' : 'inbox'"
          [message]="(view() === 'open' ? 'ops.incidents.noneOpen' : 'ops.incidents.none') | translate"
        />
      } @else {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" multiTemplateDataRows [dataSource]="rows()!">
            <ng-container matColumnDef="severity">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.incidents.colSeverity' | translate }}</th>
              <td mat-cell *matCellDef="let incident">
                <app-status-chip [status]="incident.severity" [text]="'money.incidentSeverity.' + incident.severity | translate" />
              </td>
            </ng-container>
            <ng-container matColumnDef="details">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.incidents.colDetails' | translate }}</th>
              <td mat-cell *matCellDef="let incident">{{ incident.details }}</td>
            </ng-container>
            <ng-container matColumnDef="kiosk">
              <th mat-header-cell *matHeaderCellDef>{{ 'ops.kiosk' | translate }}</th>
              <td mat-cell *matCellDef="let incident" class="neo-nowrap">
                @if (incident.kioskId) {
                  <a [routerLink]="['/kiosks', incident.kioskId]" (click)="$event.stopPropagation()">{{ incident.kioskCode ?? '' }}</a>
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.incidents.colStatus' | translate }}</th>
              <td mat-cell *matCellDef="let incident">
                <app-status-chip
                  [status]="incident.status"
                  [toneOverride]="tones[incident.status]"
                  [text]="'monitoring.incidentStatus.' + incident.status | translate"
                />
              </td>
            </ng-container>
            <ng-container matColumnDef="opened">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.incidents.colOpened' | translate }}</th>
              <td mat-cell *matCellDef="let incident" class="neo-nowrap">{{ incident.createdAt | dateTime }}</td>
            </ng-container>
            <ng-container matColumnDef="actions">
              <th mat-header-cell *matHeaderCellDef></th>
              <td mat-cell *matCellDef="let incident">
                <div class="ops__actions">
                  @if (incident.status === 'Open' && canAcknowledge()) {
                    <button mat-stroked-button type="button" [disabled]="busy() !== null" (click)="acknowledge(incident, $event)">
                      {{ 'ops.incidents.acknowledge' | translate }}
                    </button>
                  }
                  @if (incident.status !== 'Resolved' && canResolve()) {
                    <button mat-stroked-button type="button" [disabled]="busy() !== null" (click)="resolve(incident, $event)">
                      {{ 'ops.incidents.resolve' | translate }}
                    </button>
                  }
                </div>
              </td>
            </ng-container>

            <ng-container matColumnDef="detail">
              <td mat-cell *matCellDef="let incident" [attr.colspan]="columns.length">
                @if (open() === incident.id) {
                  @if (detail(); as current) {
                    <ul class="ops__timeline">
                      <li>
                        <span class="ops__when">{{ current.incident.createdAt | dateTime }}</span>
                        <span>{{ 'ops.incidents.opened' | translate }}</span>
                      </li>
                      @if (current.incident.acknowledgedAt) {
                        <li>
                          <span class="ops__when">{{ current.incident.acknowledgedAt | dateTime }}</span>
                          <span>{{ 'ops.incidents.acknowledged' | translate }}</span>
                        </li>
                      }
                      @if (current.incident.resolvedAt) {
                        <li>
                          <span class="ops__when">{{ current.incident.resolvedAt | dateTime }}</span>
                          <span>
                            {{ 'ops.incidents.resolved' | translate }}
                            @if (current.incident.resolution) {
                              <span class="neo-caption ops__block">{{ current.incident.resolution }}</span>
                            }
                          </span>
                        </li>
                      }
                    </ul>
                    <h3>{{ 'ops.incidents.affected' | translate }}</h3>
                    @for (order of current.affected; track order.jobId) {
                      <p>
                        <a [routerLink]="['/print-jobs', order.jobId]"><strong>{{ order.jobCode }}</strong></a>
                        · {{ 'jobs.status.' + order.status | translate }}
                        @if (order.paid !== null && order.paid !== undefined) {
                          · {{ order.paid | money }}
                        }
                        @if (order.paymentStatus) {
                          · {{ 'money.status.' + order.paymentStatus | translate }}
                        }
                        @for (returned of order.refunds; track returned.id) {
                          <span class="ops__block neo-caption">
                            {{ 'ops.incidents.refund' | translate: { amount: (returned.amount | money), destination: ('money.destination.' + returned.destination | translate) } }}
                            <app-status-chip [status]="returned.status" [text]="'money.refundStatus.' + returned.status | translate" />
                          </span>
                        }
                      </p>
                    } @empty {
                      <p class="neo-caption">{{ 'ops.incidents.affectedNone' | translate }}</p>
                    }
                  } @else {
                    <div class="neo-skeleton neo-skeleton--row"></div>
                  }
                }
              </td>
            </ng-container>

            <tr mat-header-row *matHeaderRowDef="columns"></tr>
            <tr
              mat-row
              *matRowDef="let row; columns: columns"
              class="ops__clickable"
              [class.ops__row--open]="open() === row.id"
              (click)="toggle(row)"
            ></tr>
            <tr mat-row *matRowDef="let row; columns: ['detail']; when: isOpen" class="ops__detail"></tr>
          </table>
        </div>
        <app-money-pager [total]="total()" [page]="page()" [pageSize]="pageSize" countKey="ops.incidents.count" (go)="go($event)" />
      }
    </div>
  `,
  styleUrl: './operations.scss',
})
export class IncidentsComponent {
  private readonly api = inject(OperationsApiService);
  private readonly auth = inject(AuthService);
  private readonly hub = inject(AdminHubService);
  private readonly kioskStore = inject(KioskLiveStore);
  private readonly incidentStore = inject(IncidentLiveStore);
  private readonly dialog = inject(MatDialog);
  private readonly toast = inject(NotifyService);
  private readonly translate = inject(TranslateService);
  readonly notify = inject(OperatorNotifyService);

  readonly pageSize = pageSize;
  readonly columns = ['severity', 'details', 'kiosk', 'status', 'opened', 'actions'];
  readonly tones = incidentStatusTones;

  readonly view = signal<IncidentView>('open');
  readonly kioskId = signal<string | null>(null);
  readonly page = signal(1);
  readonly rows = signal<readonly IncidentRow[] | null>(null);
  readonly total = signal(0);
  readonly problem = signal<Problem | null>(null);
  readonly busy = signal<string | null>(null);
  readonly open = signal<string | null>(null);
  readonly detail = signal<IncidentDetail | null>(null);

  readonly canAcknowledge = computed(() => this.auth.can(Permissions.incidentsAcknowledge));
  readonly canResolve = computed(() => this.auth.can(Permissions.incidentsResolve));
  readonly kiosks = computed(() => this.kioskStore.kiosks() ?? []);

  /** The count every page shows (`IncidentLiveStore`), not this page's own. */
  readonly openCount = computed(() => this.incidentStore.open());

  /** Which data row has its detail open below it. */
  readonly isOpen = (_index: number, row: IncidentRow): boolean => this.open() === row.id;

  constructor() {
    this.kioskStore.watch();
    this.incidentStore.watch();

    liveRefresh({
      events: [HubEvents.incidentRaised, HubEvents.incidentResolved],
      reload: () => this.load(true),
    });

    // A new Critical or High incident is said aloud: the panel may be in a background tab.
    const off = this.hub.on<IncidentEvent>(HubEvents.incidentRaised, (event) => {
      if (event.severity && loud.has(event.severity)) {
        const code = this.kioskStore.kiosks()?.find((kiosk) => kiosk.id === event.kioskId)?.code ?? '';
        this.notify.announce(
          this.translate.instant(`ops.incidents.notify.${event.severity}`) as string,
          code,
          `incident:${event.incidentId}`,
        );
      }
    });
    inject(DestroyRef).onDestroy(off);

    void this.load();
  }

  setView(view: IncidentView): void {
    this.view.set(view);
    this.restart();
  }

  setKiosk(kioskId: string | null): void {
    this.kioskId.set(kioskId);
    this.restart();
  }

  go(page: number): void {
    this.page.set(page);
    void this.load();
  }

  /** A quiet load is one the page did not ask for: what is on screen stays if it fails. */
  async load(quiet = false): Promise<void> {
    if (!quiet) {
      this.problem.set(null);
    }

    try {
      const result = await firstValueFrom(this.api.incidents(this.view(), this.kioskId(), this.page(), pageSize));
      this.rows.set(result.items);
      this.total.set(result.total);

      if (this.open() && quiet) {
        await this.loadDetail(this.open()!);
      }
    } catch (error) {
      if (!quiet) {
        this.problem.set(toProblem(error));
      }
    }
  }

  async toggle(row: IncidentRow): Promise<void> {
    if (this.open() === row.id) {
      this.open.set(null);
      this.redraw();
      return;
    }

    this.open.set(row.id);
    this.detail.set(null);
    this.redraw();
    await this.loadDetail(row.id);
  }

  /** The table works out which rows have a detail row only when its data changes. */
  private redraw(): void {
    this.rows.update((rows) => (rows ? [...rows] : rows));
  }

  async acknowledge(incident: IncidentRow, event: Event): Promise<void> {
    event.stopPropagation();
    await this.run(incident.id, async () => {
      await firstValueFrom(this.api.acknowledge(incident.id));
      this.toast.success('ops.incidents.acknowledgedToast');
    });
  }

  async resolve(incident: IncidentRow, event: Event): Promise<void> {
    event.stopPropagation();
    const text = (key: string): string => this.translate.instant(key) as string;
    const answer = await confirmAction(this.dialog, {
      title: text('ops.incidents.resolveTitle'),
      explanation: text('ops.incidents.resolveExplanation'),
      confirmLabel: text('ops.incidents.resolve'),
      cancelLabel: text('common.cancel'),
      requireReason: true,
      reasonLabel: text('ops.incidents.resolveNote'),
    });

    if (answer === undefined) {
      return;
    }

    await this.run(incident.id, async () => {
      await firstValueFrom(this.api.resolve(incident.id, answer.reason));
      this.toast.success('ops.incidents.resolvedToast');
    });
  }

  private async run(id: string, work: () => Promise<void>): Promise<void> {
    this.busy.set(id);

    try {
      await work();
      await this.load(true);
      void this.incidentStore.load();
    } catch (error) {
      this.toast.error(toProblem(error).message);
    } finally {
      this.busy.set(null);
    }
  }

  private async loadDetail(id: string): Promise<void> {
    try {
      const detail = await firstValueFrom(this.api.incident(id));

      if (this.open() === id) {
        this.detail.set(detail);
      }
    } catch (error) {
      this.toast.error(toProblem(error).message);
      this.open.set(null);
    }
  }

  private restart(): void {
    this.page.set(1);
    this.rows.set(null);
    this.open.set(null);
    void this.load();
  }
}
