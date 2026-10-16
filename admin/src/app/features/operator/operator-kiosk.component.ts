import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { KioskStatus } from '../../core/api/admin-api.service';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { AboutKiosk, HubEvents } from '../../core/realtime/hub-events';
import { KioskLiveStore } from '../../core/realtime/kiosk-live.store';
import { commandResults } from '../../core/realtime/command-results';
import { confirmAction } from '../../shared/confirm.dialog';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { BytesPipe, DateTimePipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { OperatorActionsService } from './operator-actions.service';
import { OperatorApiService } from './operator-api.service';
import { OperatorFreshnessComponent } from './operator-freshness.component';
import { OperatorKioskCardComponent } from './operator-kiosk-card.component';
import { OperatorKioskDetail } from './operator.models';
import { operatorScreen } from './operator-screen';

/**
 * "Kiosk (qısa detal)" (plan section 8B): the kiosk's card, what its computer
 * last reported, what is open for it - and what an operator standing in front
 * of it does: a refill first, then the cartridge, the test page, a pause, a
 * restart, the printers.
 */
@Component({
  selector: 'app-operator-kiosk',
  imports: [
    RouterLink,
    MatButtonModule,
    MatIconModule,
    TranslatePipe,
    ErrorBannerComponent,
    StatusChipComponent,
    BytesPipe,
    DateTimePipe,
    OperatorFreshnessComponent,
    OperatorKioskCardComponent,
  ],
  template: `
    <div class="op-page">
      <a mat-button routerLink="/operator" class="neo-back">
        <mat-icon>arrow_back</mat-icon>
        {{ 'operator.kiosk.back' | translate }}
      </a>

      @if (screen.problem(); as problem) {
        <app-error-banner [problem]="problem" effect="operator.loadFailedEffect" (retry)="screen.reload()" />
      } @else if (screen.loading()) {
        <div class="neo-skeleton neo-skeleton--block"></div>
        <div class="neo-skeleton neo-skeleton--block"></div>
      } @else if (screen.data(); as detail) {
        <app-operator-freshness [at]="screen.at()" [offline]="screen.offline()" />

        <section class="neo-card op-card">
          <app-operator-kiosk-card [card]="detail.kiosk" [heading]="true" />
        </section>

        <h2 class="op-section">{{ 'operator.kiosk.actions' | translate }}</h2>

        @if (canRefill()) {
          <button mat-flat-button type="button" class="op-primary" [disabled]="busy()" (click)="refill(detail)">
            <mat-icon>description</mat-icon>
            {{ 'operator.kiosks.refill' | translate }}
          </button>
        }

        <div class="op-actions">
          @if (canRefill()) {
            <button mat-stroked-button type="button" [disabled]="busy()" (click)="replaced(detail)">
              {{ 'operator.kiosk.replaced' | translate }}
            </button>
          }
          @if (canTestPrint()) {
            <button mat-stroked-button type="button" [disabled]="busy()" (click)="command('test-print')">
              {{ 'operator.kiosk.testPrint' | translate }}
            </button>
          }
          @if (canPause()) {
            @if (status() === 'Maintenance') {
              <button mat-stroked-button type="button" [disabled]="busy()" (click)="command('resume')">
                {{ 'operator.kiosk.resume' | translate }}
              </button>
            } @else {
              <button mat-stroked-button type="button" [disabled]="busy()" (click)="pause()">
                {{ 'operator.kiosk.pause' | translate }}
              </button>
            }
          }
          @if (canRestart()) {
            <button mat-stroked-button type="button" [disabled]="busy()" (click)="restart()">
              {{ 'operator.kiosk.restart' | translate }}
            </button>
          }
          @if (canRefreshPrinters()) {
            <button mat-stroked-button type="button" [disabled]="busy()" (click)="command('printers/refresh')">
              {{ 'operator.kiosk.refreshPrinters' | translate }}
            </button>
          }
        </div>

        <h2 class="op-section">{{ 'operator.kiosk.machine' | translate }}</h2>
        <section class="neo-card op-card">
          @if (detail.telemetry?.sample; as sample) {
            @if (detail.telemetry?.stale) {
              <p class="op-row op-row--warning">
                <mat-icon aria-hidden="true">timer_off</mat-icon>
                <span>{{ 'operator.kiosk.staleTelemetry' | translate: { time: (sample.timestamp | dateTime) } }}</span>
              </p>
            }
            <ul class="op-rows">
              @if (sample.system?.cpuPercent !== null && sample.system?.cpuPercent !== undefined) {
                <li class="op-row">
                  <mat-icon aria-hidden="true">query_stats</mat-icon>
                  <span>{{ 'operator.kiosk.cpu' | translate: { value: round(sample.system?.cpuPercent) } }}</span>
                </li>
              }
              @if (memoryPercent(detail) !== null) {
                <li class="op-row">
                  <mat-icon aria-hidden="true">storage</mat-icon>
                  <span>{{ 'operator.kiosk.memory' | translate: { value: memoryPercent(detail) } }}</span>
                </li>
              }
              @if (sample.system?.systemDiskFreeBytes; as free) {
                <li class="op-row">
                  <mat-icon aria-hidden="true">inventory_2</mat-icon>
                  <span>{{ 'operator.kiosk.disk' | translate: { value: (free | bytes) } }}</span>
                </li>
              }
              @if (sample.network?.backendLatencyMs; as latency) {
                <li class="op-row">
                  <mat-icon aria-hidden="true">network_check</mat-icon>
                  <span>{{ 'operator.kiosk.latency' | translate: { value: latency } }}</span>
                </li>
              }
              @if (sample.app?.version; as version) {
                <li class="op-row op-row--muted">
                  <mat-icon aria-hidden="true">info</mat-icon>
                  <span>{{ 'operator.kiosk.version' | translate: { value: version } }}</span>
                </li>
              }
            </ul>
          } @else {
            <p class="op-row op-row--muted">
              <mat-icon aria-hidden="true">info</mat-icon>
              <span>{{ 'operator.kiosk.noTelemetry' | translate }}</span>
            </p>
          }
        </section>

        @if (detail.tasks.length > 0) {
          <h2 class="op-section">{{ 'operator.kiosk.tasks' | translate }}</h2>
          @for (task of detail.tasks; track task.id) {
            <a class="neo-card op-card op-card--link" routerLink="/operator/tasks">
              <span class="op-card__head">
                <span class="op-card__name">
                  {{ 'operator.taskType.' + task.type | translate }}
                  <span class="op-card__sub">{{ task.notes }}</span>
                </span>
                <app-status-chip [status]="task.priority" [text]="'operator.priority.' + task.priority | translate" />
              </span>
            </a>
          }
        }

        @if (detail.incidents.length > 0) {
          <h2 class="op-section">{{ 'operator.kiosk.incidents' | translate }}</h2>
          @for (incident of detail.incidents; track incident.id) {
            <a class="neo-card op-card op-card--link" routerLink="/operator/incidents">
              <span class="op-card__head">
                <span class="op-card__name">
                  <span class="op-card__sub">{{ incident.details }}</span>
                </span>
                <app-status-chip
                  [status]="incident.severity"
                  [text]="'money.incidentSeverity.' + incident.severity | translate"
                />
              </span>
            </a>
          }
        }
      }
    </div>
  `,
  styleUrl: './operator.scss',
})
export class OperatorKioskComponent {
  private readonly api = inject(OperatorApiService);
  private readonly auth = inject(AuthService);
  private readonly actions = inject(OperatorActionsService);
  private readonly notify = inject(NotifyService);
  private readonly results = commandResults();
  private readonly dialog = inject(MatDialog);
  private readonly translate = inject(TranslateService);
  private readonly statuses = inject(KioskLiveStore);

  /** The route's `:id`, bound by the router. */
  readonly id = input.required<string>();

  readonly canRefill = computed(() => this.auth.can(Permissions.paperRefill));
  readonly canTestPrint = computed(() => this.auth.can(Permissions.printersTestPrint));
  readonly canRefreshPrinters = computed(() => this.auth.can(Permissions.printersRefresh));
  readonly canPause = computed(() => this.auth.can(Permissions.kioskCommandsPause));
  readonly canRestart = computed(() => this.auth.can(Permissions.kioskCommandsRestart));

  private readonly sending = signal(false);

  readonly screen = operatorScreen<OperatorKioskDetail>({
    key: () => `kiosk:${this.id()}`,
    fetch: () => this.api.kiosk(this.id()),
    // Everything about this kiosk, its telemetry included: this page is the
    // one place an operator watches a single machine.
    events: [
      HubEvents.kioskStatusChanged,
      HubEvents.kioskPrintersUpdated,
      HubEvents.kioskTelemetryUpdated,
      HubEvents.printerTelemetryUpdated,
      HubEvents.commandResult,
      HubEvents.queueUpdated,
      HubEvents.supplyAlertRaised,
      HubEvents.supplyAlertCleared,
      HubEvents.serviceTaskAssigned,
      HubEvents.serviceTaskUpdated,
      HubEvents.incidentRaised,
      HubEvents.incidentResolved,
    ],
    accepts: (_event, payload) => (payload as AboutKiosk | null)?.kioskId === this.id(),
    fresh: (detail) => this.statuses.learn(detail.kiosk.id, detail.kiosk.status as KioskStatus),
  });

  /** The status every page shows for this kiosk now. */
  readonly status = computed(() => this.statuses.statusOf(this.id()) ?? this.screen.data()?.kiosk.status ?? '');

  /** Nothing is sent while something is on its way, or against data the server did not give just now. */
  readonly busy = computed(() => this.sending() || !this.screen.canAct());

  constructor() {
    // The id comes from the route after construction, and changes when another kiosk is opened.
    // Only the id is followed: what the fetch reads and writes must not start it again (T-279).
    effect(() => {
      this.id();
      untracked(() => void this.screen.reload());
    });
  }

  round(value: number | null | undefined): number {
    return Math.round(value ?? 0);
  }

  memoryPercent(detail: OperatorKioskDetail): number | null {
    const system = detail.telemetry?.sample?.system;

    return system?.memoryUsedBytes && system.memoryTotalBytes
      ? Math.round((system.memoryUsedBytes / system.memoryTotalBytes) * 100)
      : null;
  }

  async refill(detail: OperatorKioskDetail): Promise<void> {
    await this.run(() => this.actions.refill(detail.kiosk));
  }

  async replaced(detail: OperatorKioskDetail): Promise<void> {
    await this.run(() => this.actions.replaced(detail.kiosk));
  }

  /** `test-print`, `resume`, `printers/refresh`: sent as they are. The result comes back as an event. */
  /** Sends a command, says it went, and later says how it ended (T-271). */
  async command(command: string): Promise<void> {
    await this.run(async () => {
      try {
        const sent = await firstValueFrom(this.api.command(this.id(), command));

        this.notify.success('operator.kiosk.commandSent');
        this.results.sent(sent);

        return true;
      } catch (error) {
        this.notify.error(toProblem(error).message);

        return false;
      }
    });
  }

  /** Stops new orders: asked about first. */
  async pause(): Promise<void> {
    if (await this.confirmed('operator.kiosk.pauseTitle', 'operator.kiosk.pauseExplanation', 'operator.kiosk.pause')) {
      await this.command('pause');
    }
  }

  /** May cut a print short: asked about first. */
  async restart(): Promise<void> {
    if (await this.confirmed('operator.kiosk.restartTitle', 'operator.kiosk.restartExplanation', 'operator.kiosk.restart')) {
      await this.command('restart-app');
    }
  }

  private async run(action: () => Promise<boolean>): Promise<void> {
    this.sending.set(true);

    try {
      if (await action()) {
        await this.screen.reload();
      }
    } finally {
      this.sending.set(false);
    }
  }

  private async confirmed(title: string, explanation: string, confirm: string): Promise<boolean> {
    const text = (key: string): string => this.translate.instant(key) as string;

    return (
      (await confirmAction(this.dialog, {
        title: text(title),
        explanation: text(explanation),
        confirmLabel: text(confirm),
        cancelLabel: text('common.cancel'),
      })) !== undefined
    );
  }
}
