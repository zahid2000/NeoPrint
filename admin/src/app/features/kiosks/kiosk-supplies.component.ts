import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MatDialog } from '@angular/material/dialog';
import { TranslateService } from '@ngx-translate/core';

import { AdminApiService } from '../../core/api/admin-api.service';
import { MonitoringApiService } from '../../core/api/monitoring-api.service';
import { SupplyRule } from '../../core/api/step7.models';
import { confirmAction } from '../../shared/confirm.dialog';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { SupplyRuleDialogComponent, SupplyRuleDialogData } from './supply-rule.dialog';
import { KioskSupplies, PaperRefillMode, PaperStock, lowPaperShare } from '../../core/api/step3.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { AboutKiosk, HubEvents } from '../../core/realtime/hub-events';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { Problem, toProblem } from '../../shared/problem';

/** The trays the panel shows, in order. */
const papers = ['A4', 'A3'] as const;

/**
 * Kiosk detail → Supplies (plan section 2.15): the paper counter per tray,
 * the refill an operator records after loading paper, and the movements
 * that changed the counter.
 */
@Component({
  selector: 'app-kiosk-supplies',
  imports: [
    FormsModule,
    RouterLink,
    MatButtonModule,
    MatButtonToggleModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatTableModule,
    TranslatePipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    StatusChipComponent,
    DateTimePipe,
  ],
  template: `
    <p class="neo-muted neo-body-small">{{ 'supplies.tabDescription' | translate }}</p>

    @if (problem(); as problem) {
      <app-error-banner [problem]="problem" effect="supplies.loadFailedEffect" (retry)="load()" />
    }

    @if (loading()) {
      <div class="neo-skeleton neo-skeleton--block"></div>
    } @else if (supplies(); as current) {
      <div class="supplies__trays">
        @for (tray of trays(); track tray.paper) {
          <div class="neo-card supplies__tray">
            <strong>{{ 'supplies.paper' + tray.paper | translate }}</strong>
            @if (tray.stock.capacity === 0) {
              <p class="neo-muted neo-body-small">{{ 'supplies.noTray' | translate }}</p>
            } @else if (tray.unset) {
              <!-- Not an empty tray: nobody has said what is in it yet (T-258). -->
              <p class="supplies__level">{{ 'supplies.counterUnset' | translate }}</p>
              <div class="neo-notice" role="status">
                <mat-icon aria-hidden="true">info</mat-icon>
                <span>{{ 'supplies.counterUnsetHint' | translate }}</span>
              </div>
            } @else {
              <p class="supplies__level">
                {{ 'supplies.level' | translate: { current: tray.stock.current, capacity: tray.stock.capacity } }}
              </p>
              <div
                class="supplies__bar"
                role="progressbar"
                [attr.aria-valuenow]="tray.stock.current"
                aria-valuemin="0"
                [attr.aria-valuemax]="tray.stock.capacity"
              >
                <span
                  class="supplies__fill"
                  [class.supplies__fill--low]="tray.low"
                  [style.width.%]="(tray.stock.current / tray.stock.capacity) * 100"
                ></span>
              </div>
              @if (tray.stock.current === 0) {
                <p class="neo-field-error" role="status">
                  <mat-icon aria-hidden="true">error</mat-icon>
                  {{ 'supplies.empty' | translate }}
                </p>
              }
              <p class="neo-caption">
                @if (tray.stock.lastRefillAt) {
                  {{ 'supplies.lastRefill' | translate: { at: (tray.stock.lastRefillAt | dateTime) } }}
                } @else {
                  {{ 'supplies.neverRefilled' | translate }}
                }
              </p>
            }
          </div>
        }
        @for (level of levels(); track level.key) {
          <div class="neo-card supplies__tray">
            <strong>{{ level.name }}</strong>
            <p class="supplies__level">{{ level.percent }}%</p>
            <div
              class="supplies__bar"
              role="progressbar"
              [attr.aria-valuenow]="level.percent"
              aria-valuemin="0"
              aria-valuemax="100"
            >
              <span
                class="supplies__fill"
                [class.supplies__fill--low]="level.alert === 'Warning'"
                [class.supplies__fill--out]="level.alert === 'Critical'"
                [style.width.%]="level.percent"
              ></span>
            </div>
            @if (level.alert) {
              <span>
                <app-status-chip
                  [status]="level.alert"
                  [toneOverride]="level.alert === 'Critical' ? 'danger' : 'warning'"
                  [text]="'monitoring.alertLevel.' + level.alert | translate"
                />
              </span>
            }
            <p class="neo-caption">{{ 'monitoring.supplies.readAt' | translate: { at: (level.readAt | dateTime) } }}</p>
          </div>
        }
      </div>

      @if (canRefill() && refillable().length > 0) {
        <section class="neo-card supplies__refill">
          <h3>{{ 'supplies.refillTitle' | translate }}</h3>
          <div class="supplies__form">
            @if (refillable().length > 1) {
              <mat-button-toggle-group
                [attr.aria-label]="'supplies.refillPaper' | translate"
                [ngModel]="paper()"
                (ngModelChange)="paper.set($event)"
              >
                @for (option of refillable(); track option) {
                  <mat-button-toggle [value]="option">{{ option }}</mat-button-toggle>
                }
              </mat-button-toggle-group>
            }
            <mat-button-toggle-group
              [attr.aria-label]="'supplies.refillMode' | translate"
              [ngModel]="mode()"
              (ngModelChange)="mode.set($event)"
            >
              @for (option of modes; track option) {
                <mat-button-toggle [value]="option">{{ 'supplies.mode.' + option | translate }}</mat-button-toggle>
              }
            </mat-button-toggle-group>
            @if (mode() !== 'Full') {
              <mat-form-field>
                <mat-label>{{ 'supplies.sheets' | translate }}</mat-label>
                <input
                  matInput
                  type="number"
                  min="0"
                  [max]="maxSheets()"
                  [ngModel]="sheets()"
                  (ngModelChange)="sheets.set($event)"
                />
              </mat-form-field>
            }
          </div>
          <p class="neo-caption">{{ 'supplies.modeHint.' + mode() | translate }}</p>
          @if (!sheetsValid() && sheets() !== null) {
            <p class="neo-field-error" role="alert">
              <mat-icon aria-hidden="true">error</mat-icon>
              {{ 'supplies.invalidSheets' | translate }}
            </p>
          }
          <div class="supplies__actions">
            <button mat-flat-button type="button" [disabled]="!sheetsValid() || saving()" (click)="refill()">
              @if (saving()) {
                <mat-spinner diameter="18" class="neo-button-spinner" />
              }
              {{ 'supplies.refill' | translate }}
            </button>
          </div>
        </section>
      }

      <h3>{{ 'monitoring.forecast.title' | translate }}</h3>
      @if ((current.forecasts ?? []).length === 0) {
        <p class="neo-caption">{{ 'monitoring.forecast.empty' | translate }}</p>
      } @else {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="current.forecasts ?? []">
            <ng-container matColumnDef="resource">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.forecast.colResource' | translate }}</th>
              <td mat-cell *matCellDef="let forecast">{{ resourceName(forecast.resource, forecast.variant) }}</td>
            </ng-container>
            <ng-container matColumnDef="current">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.forecast.colCurrent' | translate }}</th>
              <td mat-cell *matCellDef="let forecast" class="neo-nowrap">{{ amount(forecast.current, forecast.unit) }}</td>
            </ng-container>
            <ng-container matColumnDef="perDay">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.forecast.colPerDay' | translate }}</th>
              <td mat-cell *matCellDef="let forecast" class="neo-nowrap">{{ amount(forecast.perDay, forecast.unit) }}</td>
            </ng-container>
            <ng-container matColumnDef="runsOut">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.forecast.colRunsOut' | translate }}</th>
              <td mat-cell *matCellDef="let forecast" class="neo-nowrap">
                @if (forecast.runsOutAt) {
                  {{ forecast.runsOutAt | dateTime }}
                } @else {
                  <span class="neo-caption">{{ 'monitoring.forecast.noUse' | translate }}</span>
                }
              </td>
            </ng-container>

            <tr mat-header-row *matHeaderRowDef="forecastColumns"></tr>
            <tr mat-row *matRowDef="let row; columns: forecastColumns"></tr>
          </table>
        </div>
      }

      <div class="supplies__rules-head">
        <h3>{{ 'monitoring.rules.title' | translate }}</h3>
        @if (canManageRules()) {
          <button mat-stroked-button type="button" (click)="editRule(null)">
            <mat-icon>add</mat-icon>
            {{ 'monitoring.rules.add' | translate }}
          </button>
        }
      </div>
      @if (rules() === null) {
        <div class="neo-skeleton neo-skeleton--block"></div>
      } @else if (rules()!.length === 0) {
        <p class="neo-caption">{{ 'monitoring.rules.empty' | translate }}</p>
      } @else {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="rules()!">
            <ng-container matColumnDef="resource">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.forecast.colResource' | translate }}</th>
              <td mat-cell *matCellDef="let rule">
                {{ resourceName(rule.resource, rule.resourceVariant) }}
                @if (!rule.isActive) {
                  <span class="neo-caption supplies__block">{{ 'monitoring.rules.inactive' | translate }}</span>
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="warning">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.rules.colWarning' | translate }}</th>
              <td mat-cell *matCellDef="let rule" class="neo-nowrap">{{ amount(rule.warningThreshold, rule.unit) }}</td>
            </ng-container>
            <ng-container matColumnDef="critical">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.rules.colCritical' | translate }}</th>
              <td mat-cell *matCellDef="let rule" class="neo-nowrap">{{ amount(rule.criticalThreshold, rule.unit) }}</td>
            </ng-container>
            <ng-container matColumnDef="channels">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.rules.channels' | translate }}</th>
              <td mat-cell *matCellDef="let rule">{{ channelNames(rule.channels) }}</td>
            </ng-container>
            <ng-container matColumnDef="task">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.rules.colTask' | translate }}</th>
              <td mat-cell *matCellDef="let rule">
                {{ (rule.createServiceTask ? 'operator.priority.' + rule.taskPriority : 'monitoring.rules.noTask') | translate }}
              </td>
            </ng-container>
            <ng-container matColumnDef="source">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.rules.colSource' | translate }}</th>
              <td mat-cell *matCellDef="let rule">
                <app-status-chip
                  [status]="rule.isGlobal ? 'Global' : 'Kiosk'"
                  [toneOverride]="rule.isGlobal ? 'neutral' : 'info'"
                  [iconOverride]="rule.isGlobal ? 'language' : 'storefront'"
                  [text]="(rule.isGlobal ? 'monitoring.rules.global' : 'monitoring.rules.thisKiosk') | translate"
                />
              </td>
            </ng-container>
            <ng-container matColumnDef="actions">
              <th mat-header-cell *matHeaderCellDef></th>
              <td mat-cell *matCellDef="let rule" class="neo-nowrap">
                @if (canManageRules() && !rule.isGlobal) {
                  <button mat-icon-button type="button" [attr.aria-label]="'common.edit' | translate" (click)="editRule(rule)">
                    <mat-icon>edit</mat-icon>
                  </button>
                  <button mat-icon-button type="button" [attr.aria-label]="'common.delete' | translate" (click)="deleteRule(rule)">
                    <mat-icon>delete</mat-icon>
                  </button>
                }
              </td>
            </ng-container>

            <tr mat-header-row *matHeaderRowDef="ruleColumns"></tr>
            <tr mat-row *matRowDef="let row; columns: ruleColumns"></tr>
          </table>
        </div>
      }

      <h3>{{ 'supplies.eventsTitle' | translate }}</h3>
      @if (current.events.length === 0) {
        <app-empty-state icon="inventory_2" [message]="'supplies.eventsEmpty' | translate" />
      } @else {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="current.events">
            <ng-container matColumnDef="at">
              <th mat-header-cell *matHeaderCellDef>{{ 'supplies.colAt' | translate }}</th>
              <td mat-cell *matCellDef="let event">{{ event.at | dateTime }}</td>
            </ng-container>
            <ng-container matColumnDef="event">
              <th mat-header-cell *matHeaderCellDef>{{ 'supplies.colEvent' | translate }}</th>
              <td mat-cell *matCellDef="let event">
                @if (event.jobId) {
                  <a [routerLink]="['/print-jobs', event.jobId]">{{ 'supplies.event.' + event.type | translate }}</a>
                } @else {
                  {{ 'supplies.event.' + event.type | translate }}
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="paper">
              <th mat-header-cell *matHeaderCellDef>{{ 'supplies.colPaper' | translate }}</th>
              <td mat-cell *matCellDef="let event">{{ resourceKey(event.resource) | translate }}{{ variantOf(event.resource) }}</td>
            </ng-container>
            <ng-container matColumnDef="change">
              <th mat-header-cell *matHeaderCellDef>{{ 'supplies.colChange' | translate }}</th>
              <td mat-cell *matCellDef="let event">
                <span class="supplies__delta" [class.supplies__delta--added]="event.delta > 0">
                  {{ (event.delta > 0 ? '+' : '') + event.delta }}
                </span>
              </td>
            </ng-container>
            <ng-container matColumnDef="after">
              <th mat-header-cell *matHeaderCellDef>{{ 'supplies.colAfter' | translate }}</th>
              <td mat-cell *matCellDef="let event">{{ event.after }}</td>
            </ng-container>

            <tr mat-header-row *matHeaderRowDef="columns"></tr>
            <tr mat-row *matRowDef="let row; columns: columns"></tr>
          </table>
        </div>
      }
    }
  `,
  styles: `
    h3 {
      margin: var(--space-6) 0 var(--space-2);
    }

    .supplies__refill h3 {
      margin-top: 0;
    }

    .supplies__trays {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
      gap: var(--space-4);
      margin-block: var(--space-4);
    }

    .supplies__tray,
    .supplies__refill {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      padding: var(--space-4);
    }

    .supplies__refill {
      margin-block-end: var(--space-6);
    }

    .supplies__level {
      margin: 0;
      font-size: var(--font-size-h2);
      font-weight: var(--font-weight-semibold);
    }

    .supplies__bar {
      height: var(--space-2);
      border-radius: var(--radius-sm);
      background: var(--neutral-100);
      overflow: hidden;
    }

    .supplies__fill {
      display: block;
      height: 100%;
      background: var(--success-500);
    }

    .supplies__fill--low {
      background: var(--warning-500);
    }

    .supplies__fill--out {
      background: var(--danger-500);
    }

    .supplies__rules-head {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-3);
      align-items: center;
      justify-content: space-between;
      margin: var(--space-6) 0 var(--space-2);
    }

    .supplies__rules-head h3 {
      margin: 0;
    }

    .supplies__block {
      display: block;
    }

    .supplies__form {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-4);
      align-items: center;
    }

    .supplies__actions {
      display: flex;
      justify-content: flex-end;
    }

    .supplies__delta {
      font-variant-numeric: tabular-nums;
    }

    .supplies__delta--added {
      color: var(--success-500);
      font-weight: var(--font-weight-semibold);
    }
  `,
})
export class KioskSuppliesComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly auth = inject(AuthService);
  private readonly notify = inject(NotifyService);

  private readonly monitoring = inject(MonitoringApiService);
  private readonly dialog = inject(MatDialog);
  private readonly translate = inject(TranslateService);

  readonly kioskId = input.required<string>();

  /** The kiosk's code, for the rule dialog's title. */
  readonly kioskCode = input<string>('');

  readonly columns = ['at', 'event', 'paper', 'change', 'after'];
  readonly forecastColumns = ['resource', 'current', 'perDay', 'runsOut'];
  readonly ruleColumns = ['resource', 'warning', 'critical', 'channels', 'task', 'source', 'actions'];

  /** The rules that apply here: the global ones and this kiosk's own. */
  readonly rules = signal<readonly SupplyRule[] | null>(null);

  readonly canManageRules = computed(() => this.auth.can(Permissions.supplyRulesManage));

  /** What the printer says of each of its supplies, with the alert it is under, if any. */
  readonly levels = computed(() => {
    const supplies = this.supplies();
    const alerts = supplies?.alerts ?? [];

    return (supplies?.levels ?? []).map((level) => ({
      key: `${level.resource}:${level.variant ?? level.name}`,
      name: level.name,
      percent: Math.round(level.percent),
      readAt: level.readAt,
      alert:
        alerts.find(
          (alert) => alert.resource === level.resource && (!alert.variant || alert.variant === level.variant || alert.variant === level.name),
        )?.level ?? null,
    }));
  });
  readonly modes: readonly PaperRefillMode[] = ['Add', 'Full', 'Exact'];

  readonly supplies = signal<KioskSupplies | null>(null);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);
  readonly saving = signal(false);

  readonly paper = signal<string>('A4');
  readonly mode = signal<PaperRefillMode>('Add');
  readonly sheets = signal<number | null>(null);

  readonly canRefill = computed(() => this.auth.can(Permissions.paperRefill));

  readonly trays = computed(() => {
    const paper = this.supplies()?.paper ?? {};

    return papers
      .filter((name) => paper[name] !== undefined)
      .map((name) => {
        const stock: PaperStock = paper[name];

        return {
          paper: name,
          stock,
          unset: stock.capacity > 0 && stock.counted === false,
          low: stock.capacity > 0 && stock.current < stock.capacity * lowPaperShare,
        };
      });
  });

  /** Only a tray with a capacity can be filled. */
  readonly refillable = computed(() =>
    this.trays()
      .filter((tray) => tray.stock.capacity > 0)
      .map((tray) => tray.paper as string),
  );

  /** Adding may only fill the tray up to its capacity; a recount may name any count up to it. */
  readonly maxSheets = computed(() => {
    const stock = this.supplies()?.paper[this.paper()];

    return !stock ? 0 : this.mode() === 'Add' ? stock.capacity - stock.current : stock.capacity;
  });

  readonly sheetsValid = computed(() => {
    if (this.mode() === 'Full') {
      return true;
    }

    const sheets = this.sheets();
    const minimum = this.mode() === 'Add' ? 1 : 0;

    return sheets !== null && Number.isInteger(sheets) && sheets >= minimum && sheets <= this.maxSheets();
  });

  /**
   * The ledger names a tray PaperA4; the panel says "A4 kağızı" (supplies.paperA4).
   * A changed cartridge is written with which one it was - "Toner:Black Cartridge" -
   * and only the kind before the colon is a word of ours.
   */
  resourceKey(resource: string): string {
    const kind = resource.split(':')[0];

    return `supplies.${kind.charAt(0).toLowerCase()}${kind.slice(1)}`;
  }

  /** Which cartridge, as the printer names it: " (Black Cartridge)"; nothing for paper. */
  variantOf(resource: string): string {
    const colon = resource.indexOf(':');

    return colon < 0 ? '' : ` (${resource.slice(colon + 1)})`;
  }

  constructor() {
    // The counter moves by itself: an order printed, a level crossed its
    // rule, an operator finished a refill task (T-253). What is being typed
    // into the refill form stays - only the figures are fetched again.
    liveRefresh({
      events: [
        HubEvents.queueUpdated,
        HubEvents.supplyAlertRaised,
        HubEvents.supplyAlertCleared,
        HubEvents.serviceTaskUpdated,
      ],
      accepts: (_event, payload) => (payload as AboutKiosk | null)?.kioskId === this.kioskId(),
      reload: () => (this.saving() ? null : this.load(true)),
    });
  }

  ngOnInit(): void {
    void this.load();
    void this.loadRules();
  }

  async loadRules(): Promise<void> {
    try {
      const rules = await firstValueFrom(this.monitoring.supplyRules());
      const kioskId = this.kioskId();

      this.rules.set(rules.filter((rule) => rule.isGlobal || rule.kioskIds.includes(kioskId)));
    } catch {
      // The rules are secondary on this tab: without them the counters still show.
      this.rules.set([]);
    }
  }

  /** "Toner (Black Cartridge)", "A4 kağızı". */
  resourceName(resource: string, variant?: string | null): string {
    const name = this.translate.instant(`monitoring.resource.${resource}`) as string;

    return variant ? `${name} (${variant})` : name;
  }

  /** "120 vərəq", "15%". */
  amount(value: number, unit: string): string {
    const rounded = Math.round(value * 10) / 10;

    return unit === 'Percent' ? `${rounded}%` : (this.translate.instant('monitoring.unit.sheets', { value: rounded }) as string);
  }

  channelNames(channels: readonly string[]): string {
    return channels.map((channel) => this.translate.instant(`monitoring.channel.${channel}`) as string).join(', ');
  }

  editRule(rule: SupplyRule | null): void {
    const data: SupplyRuleDialogData = { rule, kioskIds: [this.kioskId()], kioskCode: this.kioskCode() };

    this.dialog
      .open(SupplyRuleDialogComponent, { data, width: '640px', maxWidth: 'calc(100vw - 32px)' })
      .afterClosed()
      .subscribe((saved) => {
        if (saved) {
          this.notify.success('monitoring.rules.saved');
          void this.loadRules();
          void this.load(true);
        }
      });
  }

  async deleteRule(rule: SupplyRule): Promise<void> {
    const text = (key: string): string => this.translate.instant(key) as string;
    const confirmed = await confirmAction(this.dialog, {
      title: text('monitoring.rules.deleteTitle'),
      explanation: text('monitoring.rules.deleteExplanation'),
      confirmLabel: text('common.delete'),
      cancelLabel: text('common.cancel'),
    });

    if (confirmed === undefined) {
      return;
    }

    try {
      await firstValueFrom(this.monitoring.deleteSupplyRule(rule.id));
      this.notify.success('monitoring.rules.deleted');
      void this.loadRules();
      void this.load(true);
    } catch (error) {
      this.notify.error(toProblem(error).message);
    }
  }

  /** A quiet fetch is one nobody asked for: one that fails leaves the tab as it is. */
  async load(quiet = false): Promise<void> {
    if (!quiet) {
      this.problem.set(null);
    }

    try {
      this.apply(await firstValueFrom(this.api.kioskSupplies(this.kioskId())));
    } catch (error) {
      if (!quiet) {
        this.problem.set(toProblem(error));
      }
    } finally {
      this.loading.set(false);
    }
  }

  async refill(): Promise<void> {
    if (!this.sheetsValid()) {
      return;
    }

    this.saving.set(true);

    try {
      const sheets = this.mode() === 'Full' ? 0 : (this.sheets() ?? 0);
      this.apply(await firstValueFrom(this.api.refillPaper(this.kioskId(), this.paper(), this.mode(), sheets)));
      this.sheets.set(null);
      this.notify.success('supplies.refilled');
    } catch (error) {
      this.notify.error(toProblem(error).message);
    } finally {
      this.saving.set(false);
    }
  }

  private apply(supplies: KioskSupplies): void {
    this.supplies.set(supplies);

    const refillable = this.refillable();

    if (!refillable.includes(this.paper()) && refillable.length > 0) {
      this.paper.set(refillable[0]);
    }
  }
}
