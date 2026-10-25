import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTableModule } from '@angular/material/table';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService } from '../../core/api/admin-api.service';
import { AgentCommandPath, MonitoringApiService } from '../../core/api/monitoring-api.service';
import {
  KioskComponent,
  KioskLog,
  KioskUpdateDetail,
  KioskUpdateState,
  ReleasesApiService,
} from '../../core/api/releases-api.service';
import { KioskCommand } from '../../core/api/step3.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { commandResults } from '../../core/realtime/command-results';
import { HubEvents } from '../../core/realtime/hub-events';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { confirmAction } from '../../shared/confirm.dialog';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { BytesPipe, DateTimePipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { Chip, updateChips } from '../releases/update-chips';

/** One of the agent's commands as a button on this tab. */
interface AgentButton {
  readonly path: AgentCommandPath;
  /** `UpdateNow`: the command's type, for its name and its texts. */
  readonly type: string;
  readonly icon: string;
  readonly danger: boolean;
}

const buttons: readonly AgentButton[] = [
  { path: 'update-now', type: 'UpdateNow', icon: 'system_update_alt', danger: false },
  { path: 'rollback-version', type: 'RollbackVersion', icon: 'undo', danger: true },
  { path: 'reapply-provisioning', type: 'ReapplyProvisioning', icon: 'build', danger: false },
  { path: 'restart-agent', type: 'RestartAgent', icon: 'restart_alt', danger: false },
  { path: 'upload-logs', type: 'UploadLogs', icon: 'cloud_upload', danger: false },
];

/** What a kiosk is doing with an update right now, most pressing first. */
const underway: readonly KioskUpdateState['status'][] = [
  'Draining',
  'Applying',
  'HealthCheck',
  'WaitingForIdle',
];

/** The notice above everything: an update under way, or one taken back. */
interface CurrentNotice {
  readonly key: 'waiting' | 'draining' | 'updating' | 'rolledBack';
  readonly chip: Chip;
  readonly state: KioskUpdateState;
}

/**
 * Kiosk detail → "Yeniləmə" (step 7B, M18; docs/DESIGN_SYSTEM.md, T-326):
 * what runs on the kiosk and what it is offered, where an update stands,
 * what happened before, the agent's five commands and the kiosk's logs.
 * Within the account's kiosk scope; each command needs its own permission.
 */
@Component({
  selector: 'app-kiosk-updates',
  imports: [
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTableModule,
    TranslatePipe,
    BytesPipe,
    DateTimePipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    StatusChipComponent,
  ],
  template: `
    @if (problem(); as problem) {
      <app-error-banner
        [problem]="problem"
        effect="kioskUpdate.loadFailedEffect"
        (retry)="load()"
      />
    } @else if (detail() === null) {
      <div class="neo-skeleton neo-skeleton--block"></div>
    } @else {
      @let current = detail()!;

      @if (notice(); as notice) {
        <div
          class="neo-notice"
          [class.neo-notice--warning]="notice.chip.tone !== 'info'"
          role="status"
          [attr.data-notice]="notice.key"
        >
          <mat-icon aria-hidden="true">{{ notice.chip.icon }}</mat-icon>
          <span>
            <strong>{{
              'kioskUpdate.notice.' + notice.key + '.title' | translate: noticeParams(notice)
            }}</strong>
            {{ 'kioskUpdate.notice.' + notice.key + '.body' | translate: noticeParams(notice) }}
          </span>
        </div>
      }

      <h3>{{ 'kioskUpdate.versionsTitle' | translate }}</h3>
      <div class="neo-table-wrapper">
        <table mat-table class="neo-table" [dataSource]="current.components" data-table="versions">
          <ng-container matColumnDef="component">
            <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.component' | translate }}</th>
            <td mat-cell *matCellDef="let c">
              {{ 'releases.component.' + c.component | translate }}
            </td>
          </ng-container>
          <ng-container matColumnDef="running">
            <th mat-header-cell *matHeaderCellDef>{{ 'kioskUpdate.col.running' | translate }}</th>
            <td mat-cell *matCellDef="let c" class="neo-nowrap">{{ c.running }}</td>
          </ng-container>
          <ng-container matColumnDef="target">
            <th mat-header-cell *matHeaderCellDef>{{ 'kioskUpdate.col.target' | translate }}</th>
            <td mat-cell *matCellDef="let c" class="neo-nowrap">{{ targetOf(c) }}</td>
          </ng-container>
          <ng-container matColumnDef="state">
            <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.update' | translate }}</th>
            <td mat-cell *matCellDef="let c">
              @if (c.state; as state) {
                <app-status-chip
                  [status]="state.status"
                  [toneOverride]="chipOf(state).tone"
                  [iconOverride]="chipOf(state).icon"
                  [text]="
                    ('releases.updateStatus.' + state.status | translate) + ' · ' + state.version
                  "
                />
                @if (state.message) {
                  <span class="neo-row-note">{{ state.message }}</span>
                }
              }
            </td>
          </ng-container>

          <tr mat-header-row *matHeaderRowDef="versionColumns"></tr>
          <tr mat-row *matRowDef="let row; columns: versionColumns"></tr>
        </table>
      </div>
      <p class="neo-caption">
        {{
          'kioskUpdate.caption'
            | translate
              : {
                  channel: ('releases.channel.' + current.channel | translate),
                  platform: current.platform
                    ? ('releases.platform.' + current.platform | translate)
                    : '—',
                }
        }}
        @if (current.lastHeartbeatAt) {
          · {{ 'kioskUpdate.lastSeen' | translate: { time: (current.lastHeartbeatAt | dateTime) } }}
        }
      </p>

      @if (current.skipped.length > 0) {
        <div class="neo-notice neo-notice--warning" role="status" data-notice="skipped">
          <mat-icon aria-hidden="true">block</mat-icon>
          <span>{{ 'kioskUpdate.skipped' | translate: { versions: skippedText(current) } }}</span>
        </div>
      }

      @if (visibleButtons().length > 0) {
        <h3>{{ 'kioskUpdate.commandsTitle' | translate }}</h3>
        @if (current.hasDevice) {
          <div class="updates__actions">
            @for (button of visibleButtons(); track button.path) {
              <button
                mat-stroked-button
                type="button"
                [class.updates__danger]="button.danger"
                [attr.data-command]="button.path"
                [disabled]="busy() !== null"
                (click)="ask(button)"
              >
                @if (busy() === button.path) {
                  <mat-spinner diameter="18" class="neo-button-spinner" />
                } @else {
                  <mat-icon>{{ button.icon }}</mat-icon>
                }
                {{ 'printers.command.' + button.type | translate }}
              </button>
            }
          </div>
          <p class="neo-caption">{{ 'kioskUpdate.commandsHint' | translate }}</p>
        } @else {
          <p class="neo-caption" data-no-device>{{ 'kioskUpdate.noDevice' | translate }}</p>
        }
      }

      <h3>{{ 'kioskUpdate.historyTitle' | translate }}</h3>
      @if (current.history.length === 0) {
        <app-empty-state icon="history" [message]="'kioskUpdate.historyEmpty' | translate" />
      } @else {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="current.history" data-table="history">
            <ng-container matColumnDef="at">
              <th mat-header-cell *matHeaderCellDef>{{ 'kioskUpdate.col.at' | translate }}</th>
              <td mat-cell *matCellDef="let h" class="neo-nowrap">{{ h.reportedAt | dateTime }}</td>
            </ng-container>
            <ng-container matColumnDef="component">
              <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.component' | translate }}</th>
              <td mat-cell *matCellDef="let h">
                {{ 'releases.component.' + h.component | translate }}
              </td>
            </ng-container>
            <ng-container matColumnDef="version">
              <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.version' | translate }}</th>
              <td mat-cell *matCellDef="let h" class="neo-nowrap">{{ h.version }}</td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.status' | translate }}</th>
              <td mat-cell *matCellDef="let h">
                <app-status-chip
                  [status]="h.status"
                  [toneOverride]="chipOf(h).tone"
                  [iconOverride]="chipOf(h).icon"
                  [text]="'releases.updateStatus.' + h.status | translate"
                />
              </td>
            </ng-container>
            <ng-container matColumnDef="message">
              <th mat-header-cell *matHeaderCellDef>{{ 'kioskUpdate.col.message' | translate }}</th>
              <td mat-cell *matCellDef="let h">{{ h.message }}</td>
            </ng-container>

            <tr mat-header-row *matHeaderRowDef="historyColumns"></tr>
            <tr mat-row *matRowDef="let row; columns: historyColumns"></tr>
          </table>
        </div>
      }

      @if (canLogs()) {
        <h3>{{ 'kioskUpdate.logsTitle' | translate }}</h3>
        @if (logUpload(); as upload) {
          <div class="updates__upload" data-log-upload>
            @if (upload.status === 'Sent' || upload.status === 'Acknowledged') {
              <app-status-chip
                status="uploading"
                toneOverride="info"
                iconOverride="progress_activity"
                [text]="'kioskUpdate.logsUploading' | translate"
              />
            } @else if (upload.status === 'Failed' || upload.status === 'Expired') {
              <app-status-chip
                status="failed"
                toneOverride="danger"
                iconOverride="error"
                [text]="'kioskUpdate.logsFailed' | translate"
              />
              @if (upload.result) {
                <span class="neo-row-note">{{ upload.result }}</span>
              }
            }
          </div>
        }

        @if (logsProblem(); as problem) {
          <app-error-banner
            [problem]="problem"
            effect="kioskUpdate.logsLoadFailedEffect"
            (retry)="loadLogs()"
          />
        } @else if (logs() === null) {
          <div class="neo-skeleton neo-skeleton--block"></div>
        } @else if (logs()!.length === 0) {
          <app-empty-state icon="description" [message]="'kioskUpdate.logsEmpty' | translate">
            @if (current.hasDevice) {
              <button
                mat-stroked-button
                type="button"
                [disabled]="busy() !== null"
                (click)="ask(uploadButton)"
              >
                <mat-icon>cloud_upload</mat-icon>{{ 'printers.command.UploadLogs' | translate }}
              </button>
            }
          </app-empty-state>
        } @else {
          <div class="neo-table-wrapper">
            <table mat-table class="neo-table" [dataSource]="logs()!" data-table="logs">
              <ng-container matColumnDef="uploaded">
                <th mat-header-cell *matHeaderCellDef>
                  {{ 'kioskUpdate.col.uploaded' | translate }}
                </th>
                <td mat-cell *matCellDef="let log" class="neo-nowrap">
                  {{ log.createdAt | dateTime }}
                </td>
              </ng-container>
              <ng-container matColumnDef="lines">
                <th mat-header-cell *matHeaderCellDef>{{ 'kioskUpdate.col.lines' | translate }}</th>
                <td mat-cell *matCellDef="let log">{{ log.lines }}</td>
              </ng-container>
              <ng-container matColumnDef="size">
                <th mat-header-cell *matHeaderCellDef>{{ 'kioskUpdate.col.size' | translate }}</th>
                <td mat-cell *matCellDef="let log" class="neo-nowrap">{{ log.size | bytes }}</td>
              </ng-container>
              <ng-container matColumnDef="redacted">
                <th mat-header-cell *matHeaderCellDef>
                  {{ 'kioskUpdate.col.redacted' | translate }}
                </th>
                <td mat-cell *matCellDef="let log">
                  @if (log.redacted > 0) {
                    {{ 'kioskUpdate.redacted' | translate: { count: log.redacted } }}
                  }
                </td>
              </ng-container>
              <ng-container matColumnDef="download">
                <th mat-header-cell *matHeaderCellDef>
                  <span class="cdk-visually-hidden">{{ 'kioskUpdate.download' | translate }}</span>
                </th>
                <td mat-cell *matCellDef="let log">
                  <button
                    mat-icon-button
                    type="button"
                    [attr.data-download]="log.id"
                    [attr.aria-label]="'kioskUpdate.download' | translate"
                    [disabled]="downloading() === log.id"
                    (click)="download(log)"
                  >
                    <mat-icon>download</mat-icon>
                  </button>
                </td>
              </ng-container>

              <tr mat-header-row *matHeaderRowDef="logColumns"></tr>
              <tr mat-row *matRowDef="let row; columns: logColumns"></tr>
            </table>
          </div>
        }
      }
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--space-3);
    }

    h3 {
      margin: var(--space-4) 0 0;
    }

    .neo-caption {
      margin: 0;
    }

    .updates__actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-3);
    }

    .updates__danger:not(:disabled) {
      color: var(--danger-500);
    }

    .updates__upload {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      align-items: center;
    }
  `,
})
export class KioskUpdatesComponent implements OnInit {
  private readonly releases = inject(ReleasesApiService);
  private readonly admin = inject(AdminApiService);
  private readonly api = inject(MonitoringApiService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly translate = inject(TranslateService);
  private readonly results = commandResults();

  readonly kioskId = input.required<string>();

  /** For the downloaded log's name. */
  readonly kioskCode = input<string>('');

  readonly versionColumns = ['component', 'running', 'target', 'state'];
  readonly historyColumns = ['at', 'component', 'version', 'status', 'message'];
  readonly logColumns = ['uploaded', 'lines', 'size', 'redacted', 'download'];

  readonly detail = signal<KioskUpdateDetail | null>(null);
  readonly problem = signal<Problem | null>(null);
  readonly logs = signal<KioskLog[] | null>(null);
  readonly logsProblem = signal<Problem | null>(null);
  readonly commands = signal<KioskCommand[]>([]);
  readonly busy = signal<AgentCommandPath | null>(null);
  readonly downloading = signal<string | null>(null);

  readonly canUpdate = computed(() => this.auth.can(Permissions.kioskCommandsUpdate));
  readonly canRestart = computed(() => this.auth.can(Permissions.kioskCommandsRestart));
  readonly canLogs = computed(() => this.auth.can(Permissions.kioskLogsDownload));

  /** Only the commands this account may send (the kiosk scope is the server's to hold). */
  readonly visibleButtons = computed(() =>
    buttons.filter((button) =>
      button.path === 'restart-agent'
        ? this.canRestart()
        : button.path === 'upload-logs'
          ? this.canLogs()
          : this.canUpdate(),
    ),
  );

  readonly uploadButton = buttons.find((button) => button.path === 'upload-logs')!;

  /** An update under way says so first; otherwise the last one taken back. */
  readonly notice = computed<CurrentNotice | null>(() => {
    const states = (this.detail()?.components ?? [])
      .map((component) => component.state)
      .filter((state) => state !== null);

    for (const status of underway) {
      const state = states.find((candidate) => candidate.status === status);
      if (state) {
        const key =
          status === 'Draining' ? 'draining' : status === 'WaitingForIdle' ? 'waiting' : 'updating';
        return { key, chip: updateChips[status], state };
      }
    }

    const back = states.find((state) => state.status === 'RolledBack');
    return back ? { key: 'rolledBack', chip: updateChips.RolledBack, state: back } : null;
  });

  /** The last "Logları yüklə": while it is open, the logs are on their way. */
  readonly logUpload = computed(
    () => this.commands().find((command) => command.type === 'UploadLogs') ?? null,
  );

  constructor() {
    liveRefresh({
      events: [HubEvents.kioskUpdateChanged, HubEvents.commandResult],
      accepts: (_, payload) => (payload as { kioskId?: string } | null)?.kioskId === this.kioskId(),
      reload: () => this.load(true),
    });
  }

  ngOnInit(): void {
    void this.load();
  }

  async load(quiet = false): Promise<void> {
    if (!quiet) {
      this.problem.set(null);
    }

    try {
      this.detail.set(await firstValueFrom(this.releases.kioskUpdate(this.kioskId())));
    } catch (error) {
      if (!quiet) {
        this.problem.set(toProblem(error));
      }
    }

    // The log upload's state is in the kiosk's commands; quietly - the "Əmrlər" tab says it out loud.
    try {
      this.commands.set(await firstValueFrom(this.admin.kioskCommands(this.kioskId())));
    } catch {
      // Not shown: the upload chip simply stays as it was.
    }

    if (this.canLogs()) {
      await this.loadLogs(quiet);
    }
  }

  async loadLogs(quiet = false): Promise<void> {
    if (!quiet) {
      this.logsProblem.set(null);
    }

    try {
      this.logs.set(await firstValueFrom(this.releases.kioskLogs(this.kioskId())));
    } catch (error) {
      if (!quiet) {
        this.logsProblem.set(toProblem(error));
      }
    }
  }

  chipOf(state: KioskUpdateState): Chip {
    return updateChips[state.status];
  }

  /** The offered version, when it is not what already runs. */
  targetOf(component: KioskComponent): string {
    return component.target && component.target !== component.running ? component.target : '';
  }

  noticeParams(notice: CurrentNotice): Record<string, unknown> {
    return {
      component: this.t('releases.component.' + notice.state.component),
      version: notice.state.version,
      minutes: this.detail()?.maxIdleWaitMinutes ?? 0,
      message: notice.state.message ?? '',
    };
  }

  skippedText(detail: KioskUpdateDetail): string {
    return detail.skipped
      .map((item) => `${this.t('releases.component.' + item.component)} ${item.version}`)
      .join(', ');
  }

  /** Every command is asked about first, with a reason that goes into the audit log (T-326). */
  async ask(button: AgentButton): Promise<void> {
    const key = `kioskUpdate.command.${button.type}`;
    const confirmed = await confirmAction(this.dialog, {
      title: this.t(`${key}.title`),
      explanation: this.t(`${key}.explanation`),
      confirmLabel: this.t(`printers.command.${button.type}`),
      cancelLabel: this.t('common.cancel'),
      destructive: button.danger,
      requireReason: true,
      reasonLabel: this.t('kioskUpdate.reason'),
      reasonHint: this.t('kioskUpdate.reasonHint'),
      minReasonLength: 3,
      maxReasonLength: 500,
    });

    if (confirmed !== undefined) {
      await this.send(button, confirmed.reason);
    }
  }

  async download(log: KioskLog): Promise<void> {
    this.downloading.set(log.id);

    try {
      const blob = await firstValueFrom(this.releases.kioskLogFile(this.kioskId(), log.id));
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `${this.kioskCode() || 'kiosk'}-${log.createdAt.replace(/[:.]/g, '-')}.log.gz`;
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (error) {
      this.notify.error(toProblem(error).message);
    } finally {
      this.downloading.set(null);
    }
  }

  private async send(button: AgentButton, reason: string): Promise<void> {
    this.busy.set(button.path);

    try {
      const sent = await firstValueFrom(this.api.command(this.kioskId(), button.path, reason));
      this.commands.update((list) => [sent, ...list.filter((item) => item.id !== sent.id)]);
      this.notify.success('printers.sent');
      this.results.sent(sent);
    } catch (error) {
      this.notify.error(toProblem(error).message);
    } finally {
      this.busy.set(null);
    }
  }

  private t(key: string, params?: Record<string, unknown>): string {
    return this.translate.instant(key, params) as string;
  }
}
