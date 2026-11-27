import { Component, OnInit, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { OperationsApiService } from '../../core/api/operations-api.service';
import { MonitoringStatus } from '../../core/api/step7.models';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';

/** The dashboards Grafana has, by uid (docker/monitoring/grafana/dashboards). */
const dashboards = [
  { uid: 'overview', label: 'sysmon.dash.overview' },
  { uid: 'api', label: 'sysmon.dash.api' },
  { uid: 'queues', label: 'sysmon.dash.queues' },
  { uid: 'operations', label: 'sysmon.dash.operations' },
] as const;

/**
 * "Monitorinq" (plan step 8, T-477; docs/MONITORING.md): whether the API's
 * metrics flow, and the way to Grafana - which has its own sign-in, so this
 * page holds no chart and no password.
 */
@Component({
  selector: 'app-system-monitoring',
  imports: [MatButtonModule, MatIconModule, TranslatePipe, PageHeaderComponent, ErrorBannerComponent, StatusChipComponent, DateTimePipe],
  template: `
    <div class="neo-page">
      <app-page-header [title]="'sysmon.title' | translate" [description]="'sysmon.description' | translate" />

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="sysmon.loadFailedEffect" (retry)="load()" />
      }

      @if (status(); as s) {
        <section class="neo-card sysmon__card" data-test="status">
          @if (s.metricsEnabled) {
            <app-status-chip status="metrics-on" toneOverride="success" iconOverride="check_circle" [text]="'sysmon.metricsOn' | translate" />
            <p class="neo-caption" data-test="counted">
              @if (s.countedAt) {
                {{ 'sysmon.countedAt' | translate: { time: (s.countedAt | dateTime) } }}
              } @else {
                {{ 'sysmon.notCounted' | translate }}
              }
            </p>
          } @else {
            <app-status-chip status="metrics-off" toneOverride="warning" iconOverride="cloud_off" [text]="'sysmon.metricsOff' | translate" />
            <p class="neo-caption">{{ 'sysmon.metricsOffHint' | translate }}</p>
          }
        </section>

        <section class="neo-card sysmon__card">
          @if (s.grafanaUrl; as url) {
            <a mat-flat-button color="primary" data-test="open" [href]="url" target="_blank" rel="noopener noreferrer">
              <mat-icon>open_in_new</mat-icon>{{ 'sysmon.open' | translate }}
            </a>
            <p class="neo-caption">{{ 'sysmon.loginNote' | translate }}</p>
            <h2 class="sysmon__heading">{{ 'sysmon.dashboards' | translate }}</h2>
            <ul class="sysmon__list">
              @for (d of dashboards; track d.uid) {
                <li>
                  <a [href]="url.replace(trailingSlash, '') + '/d/neoprint-' + d.uid" target="_blank" rel="noopener noreferrer" [attr.data-dashboard]="d.uid">
                    {{ d.label | translate }}
                  </a>
                </li>
              }
            </ul>
          } @else {
            <div class="neo-notice neo-notice--warning" data-test="no-grafana">
              <mat-icon>link_off</mat-icon>
              <span>{{ 'sysmon.noGrafana' | translate }}</span>
            </div>
          }
        </section>
      }
    </div>
  `,
  styles: `
    .sysmon__card {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
      gap: var(--space-3);
      margin-bottom: var(--space-4);
    }
    .sysmon__heading {
      margin: 0;
      font-size: var(--font-size-h2);
      font-weight: var(--font-weight-semibold);
    }
    .sysmon__list {
      margin: 0;
      padding-left: var(--space-6);
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
    }
  `,
})
export class SystemMonitoringComponent implements OnInit {
  private readonly api = inject(OperationsApiService);

  readonly dashboards = dashboards;
  readonly trailingSlash = /\/+$/;

  readonly status = signal<MonitoringStatus | null>(null);
  readonly problem = signal<Problem | null>(null);

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    try {
      this.status.set(await firstValueFrom(this.api.monitoringStatus()));
      this.problem.set(null);
    } catch (error) {
      this.problem.set(toProblem(error));
    }
  }
}
