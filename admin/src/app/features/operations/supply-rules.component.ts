import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MonitoringApiService } from '../../core/api/monitoring-api.service';
import { OperationsApiService } from '../../core/api/operations-api.service';
import { SupplyAlertRow, SupplyRule } from '../../core/api/step7.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { HubEvents } from '../../core/realtime/hub-events';
import { KioskLiveStore } from '../../core/realtime/kiosk-live.store';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { confirmAction } from '../../shared/confirm.dialog';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { SupplyRuleDialogComponent, SupplyRuleDialogData } from '../kiosks/supply-rule.dialog';

/**
 * "Xəbərdarlıq qaydaları" (plan sections 2.15, 8A.3): what is below its rule
 * now, live, and the rules themselves - for every kiosk or for some, the two
 * limits, who is told and whether a service task opens.
 */
@Component({
  selector: 'app-supply-rules',
  imports: [
    RouterLink,
    MatButtonModule,
    MatIconModule,
    MatTableModule,
    TranslatePipe,
    DateTimePipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    PageHeaderComponent,
    StatusChipComponent,
  ],
  template: `
    <div class="neo-page">
      <app-page-header [title]="'ops.rules.title' | translate" [description]="'ops.rules.description' | translate">
        @if (canManage()) {
          <button mat-flat-button type="button" (click)="edit(null)">
            <mat-icon>add</mat-icon>
            {{ 'ops.rules.new' | translate }}
          </button>
        }
      </app-page-header>

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="ops.rules.loadFailedEffect" (retry)="load()" />
      }

      <section class="neo-card ops__card">
        <h2>{{ 'ops.rules.alertsTitle' | translate }}</h2>
        @if (alerts() === null) {
          <div class="neo-skeleton neo-skeleton--row"></div>
        } @else if (alerts()!.length === 0) {
          <p>
            <app-status-chip status="Ok" toneOverride="success" [text]="'ops.rules.noAlerts' | translate" />
          </p>
        } @else {
          <div class="neo-table-wrapper">
            <table mat-table class="neo-table" [dataSource]="alerts()!">
              <ng-container matColumnDef="kiosk">
                <th mat-header-cell *matHeaderCellDef>{{ 'ops.kiosk' | translate }}</th>
                <td mat-cell *matCellDef="let alert" class="neo-nowrap">
                  <a [routerLink]="['/kiosks', alert.kioskId]">{{ alert.kioskCode }}</a>
                </td>
              </ng-container>
              <ng-container matColumnDef="resource">
                <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.forecast.colResource' | translate }}</th>
                <td mat-cell *matCellDef="let alert">{{ resourceName(alert.resource, alert.variant) }}</td>
              </ng-container>
              <ng-container matColumnDef="level">
                <th mat-header-cell *matHeaderCellDef>{{ 'ops.rules.level' | translate }}</th>
                <td mat-cell *matCellDef="let alert">
                  <app-status-chip
                    [status]="alert.level"
                    [toneOverride]="alert.level === 'Critical' ? 'danger' : 'warning'"
                    [text]="'monitoring.alertLevel.' + alert.level | translate"
                  />
                </td>
              </ng-container>
              <ng-container matColumnDef="value">
                <th mat-header-cell *matHeaderCellDef class="ops__number">{{ 'ops.rules.value' | translate }}</th>
                <td mat-cell *matCellDef="let alert" class="ops__number">{{ amount(alert.value, alert.unit) }}</td>
              </ng-container>
              <ng-container matColumnDef="since">
                <th mat-header-cell *matHeaderCellDef>{{ 'ops.rules.since' | translate }}</th>
                <td mat-cell *matCellDef="let alert" class="neo-nowrap">{{ alert.raisedAt | dateTime }}</td>
              </ng-container>
              <tr mat-header-row *matHeaderRowDef="alertColumns"></tr>
              <tr mat-row *matRowDef="let row; columns: alertColumns"></tr>
            </table>
          </div>
        }
      </section>

      <h2>{{ 'ops.rules.rulesTitle' | translate }}</h2>
      @if (rules() === null) {
        <div class="neo-skeleton neo-skeleton--block"></div>
      } @else if (rules()!.length === 0) {
        <app-empty-state icon="notifications_active" [message]="'ops.rules.empty' | translate" [hint]="'ops.rules.emptyHint' | translate" />
      } @else {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="rules()!">
            <ng-container matColumnDef="name">
              <th mat-header-cell *matHeaderCellDef>{{ 'ops.rules.name' | translate }}</th>
              <td mat-cell *matCellDef="let rule">{{ rule.name }}</td>
            </ng-container>
            <ng-container matColumnDef="resource">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.forecast.colResource' | translate }}</th>
              <td mat-cell *matCellDef="let rule">{{ resourceName(rule.resource, rule.resourceVariant) }}</td>
            </ng-container>
            <ng-container matColumnDef="where">
              <th mat-header-cell *matHeaderCellDef>{{ 'ops.rules.where' | translate }}</th>
              <td mat-cell *matCellDef="let rule">
                @if (rule.isGlobal) {
                  <app-status-chip status="Global" toneOverride="neutral" iconOverride="language" [text]="'monitoring.rules.global' | translate" />
                } @else {
                  {{ codes(rule.kioskIds) }}
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="warning">
              <th mat-header-cell *matHeaderCellDef class="ops__number">{{ 'monitoring.rules.colWarning' | translate }}</th>
              <td mat-cell *matCellDef="let rule" class="ops__number">{{ amount(rule.warningThreshold, rule.unit) }}</td>
            </ng-container>
            <ng-container matColumnDef="critical">
              <th mat-header-cell *matHeaderCellDef class="ops__number">{{ 'monitoring.rules.colCritical' | translate }}</th>
              <td mat-cell *matCellDef="let rule" class="ops__number">{{ amount(rule.criticalThreshold, rule.unit) }}</td>
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
            <ng-container matColumnDef="state">
              <th mat-header-cell *matHeaderCellDef>{{ 'monitoring.tasks.colStatus' | translate }}</th>
              <td mat-cell *matCellDef="let rule">
                <app-status-chip
                  [status]="rule.isActive ? 'Active' : 'Inactive'"
                  [text]="(rule.isActive ? 'ops.rules.on' : 'monitoring.rules.inactive') | translate"
                />
              </td>
            </ng-container>
            <ng-container matColumnDef="actions">
              <th mat-header-cell *matHeaderCellDef></th>
              <td mat-cell *matCellDef="let rule" class="neo-nowrap">
                @if (canManage()) {
                  <button mat-icon-button type="button" [attr.aria-label]="'common.edit' | translate" (click)="edit(rule)">
                    <mat-icon>edit</mat-icon>
                  </button>
                  <button mat-icon-button type="button" [attr.aria-label]="'common.delete' | translate" (click)="remove(rule)">
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
    </div>
  `,
  styles: `
    h2 {
      margin: 0 0 var(--space-3);
      font-size: var(--font-size-h2);
    }

    .ops__card {
      padding: var(--space-4);
      margin-block-end: var(--space-6);
    }
  `,
  styleUrl: './operations.scss',
})
export class SupplyRulesComponent {
  private readonly monitoring = inject(MonitoringApiService);
  private readonly api = inject(OperationsApiService);
  private readonly auth = inject(AuthService);
  private readonly kioskStore = inject(KioskLiveStore);
  private readonly dialog = inject(MatDialog);
  private readonly toast = inject(NotifyService);
  private readonly translate = inject(TranslateService);

  readonly alertColumns = ['kiosk', 'resource', 'level', 'value', 'since'];
  readonly ruleColumns = ['name', 'resource', 'where', 'warning', 'critical', 'channels', 'task', 'state', 'actions'];

  readonly rules = signal<readonly SupplyRule[] | null>(null);
  readonly alerts = signal<readonly SupplyAlertRow[] | null>(null);
  readonly problem = signal<Problem | null>(null);

  readonly canManage = computed(() => this.auth.can(Permissions.supplyRulesManage));

  constructor() {
    this.kioskStore.watch();

    liveRefresh({
      events: [HubEvents.supplyAlertRaised, HubEvents.supplyAlertCleared],
      reload: () => this.loadAlerts(),
    });

    void this.load();
  }

  async load(): Promise<void> {
    this.problem.set(null);

    try {
      this.rules.set(await firstValueFrom(this.monitoring.supplyRules()));
    } catch (error) {
      this.problem.set(toProblem(error));
    }

    await this.loadAlerts();
  }

  async loadAlerts(): Promise<void> {
    try {
      this.alerts.set((await firstValueFrom(this.api.activeAlerts())).items);
    } catch {
      this.alerts.set(this.alerts() ?? []);
    }
  }

  codes(kioskIds: readonly string[]): string {
    const known = new Map((this.kioskStore.kiosks() ?? []).map((kiosk) => [kiosk.id, kiosk.code]));

    return kioskIds.map((id) => known.get(id) ?? '').filter(Boolean).join(', ');
  }

  resourceName(resource: string, variant?: string | null): string {
    const name = this.translate.instant(`monitoring.resource.${resource}`) as string;

    return variant ? `${name} (${variant})` : name;
  }

  amount(value: number, unit: string): string {
    const rounded = Math.round(value * 10) / 10;

    return unit === 'Percent' ? `${rounded}%` : (this.translate.instant('monitoring.unit.sheets', { value: rounded }) as string);
  }

  channelNames(channels: readonly string[]): string {
    return channels.map((channel) => this.translate.instant(`monitoring.channel.${channel}`) as string).join(', ');
  }

  edit(rule: SupplyRule | null): void {
    const data: SupplyRuleDialogData = { rule, kioskIds: [], kioskCode: '', kiosks: this.kioskStore.kiosks() ?? [] };

    this.dialog
      .open(SupplyRuleDialogComponent, { data, width: '680px', maxWidth: 'calc(100vw - 32px)' })
      .afterClosed()
      .subscribe((saved) => {
        if (saved) {
          this.toast.success('ops.rules.saved');
          void this.load();
        }
      });
  }

  async remove(rule: SupplyRule): Promise<void> {
    const text = (key: string): string => this.translate.instant(key) as string;
    const confirmed = await confirmAction(this.dialog, {
      title: text('ops.rules.deleteTitle'),
      explanation: text('ops.rules.deleteExplanation'),
      confirmLabel: text('common.delete'),
      cancelLabel: text('common.cancel'),
    });

    if (confirmed === undefined) {
      return;
    }

    try {
      await firstValueFrom(this.monitoring.deleteSupplyRule(rule.id));
      this.toast.success('monitoring.rules.deleted');
      void this.load();
    } catch (error) {
      this.toast.error(toProblem(error).message);
    }
  }
}
