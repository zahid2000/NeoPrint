import { Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTableModule } from '@angular/material/table';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, KioskStatus } from '../../core/api/admin-api.service';
import { KioskCommandPath, MonitoringApiService } from '../../core/api/monitoring-api.service';
import { KioskCommand } from '../../core/api/step3.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { commandResults } from '../../core/realtime/command-results';
import { HubEvents } from '../../core/realtime/hub-events';
import { KioskLiveStore } from '../../core/realtime/kiosk-live.store';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { confirmAction } from '../../shared/confirm.dialog';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';

/**
 * Kiosk detail → "Əmrlər" (plan sections 7.8, 8A.3): pause and resume, a
 * restart of the application or of the computer, and every command the kiosk
 * was sent with how it ended - live, as the kiosk answers.
 */
@Component({
  selector: 'app-kiosk-commands',
  imports: [
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTableModule,
    TranslatePipe,
    DateTimePipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    StatusChipComponent,
  ],
  template: `
    <p class="neo-muted neo-body-small">{{ 'monitoring.commands.description' | translate }}</p>

    @if (canPause() || canRestart() || canReboot()) {
      <div class="commands__actions">
        @if (canPause()) {
          @if (paused()) {
            <button mat-stroked-button type="button" [disabled]="busy() !== null" (click)="send('resume')">
              @if (busy() === 'resume') {
                <mat-spinner diameter="18" class="neo-button-spinner" />
              } @else {
                <mat-icon>play_arrow</mat-icon>
              }
              {{ 'monitoring.commands.resume' | translate }}
            </button>
          } @else {
            <button mat-stroked-button type="button" [disabled]="busy() !== null" (click)="ask('pause')">
              @if (busy() === 'pause') {
                <mat-spinner diameter="18" class="neo-button-spinner" />
              } @else {
                <mat-icon>pause</mat-icon>
              }
              {{ 'monitoring.commands.pause' | translate }}
            </button>
          }
        }
        @if (canRestart()) {
          <button mat-stroked-button type="button" [disabled]="busy() !== null" (click)="ask('restart-app')">
            @if (busy() === 'restart-app') {
              <mat-spinner diameter="18" class="neo-button-spinner" />
            } @else {
              <mat-icon>refresh</mat-icon>
            }
            {{ 'monitoring.commands.restart' | translate }}
          </button>
        }
        @if (canReboot()) {
          <button
            mat-stroked-button
            type="button"
            class="commands__danger"
            [disabled]="busy() !== null"
            (click)="ask('reboot')"
          >
            @if (busy() === 'reboot') {
              <mat-spinner diameter="18" class="neo-button-spinner" />
            } @else {
              <mat-icon>restart_alt</mat-icon>
            }
            {{ 'monitoring.commands.reboot' | translate }}
          </button>
        }
      </div>
    }

    <h3>{{ 'printers.commandsTitle' | translate }}</h3>

    @if (problem(); as problem) {
      <app-error-banner [problem]="problem" effect="monitoring.commands.loadFailedEffect" (retry)="load()" />
    } @else if (commands() === null) {
      <div class="neo-skeleton neo-skeleton--block"></div>
    } @else if (commands()!.length === 0) {
      <app-empty-state icon="terminal" [message]="'printers.commandsEmpty' | translate" />
    } @else {
      <div class="neo-table-wrapper">
        <table mat-table class="neo-table" [dataSource]="commands()!">
          <ng-container matColumnDef="type">
            <th mat-header-cell *matHeaderCellDef>{{ 'printers.colCommand' | translate }}</th>
            <td mat-cell *matCellDef="let command">{{ 'printers.command.' + command.type | translate }}</td>
          </ng-container>
          <ng-container matColumnDef="status">
            <th mat-header-cell *matHeaderCellDef>{{ 'printers.colStatus' | translate }}</th>
            <td mat-cell *matCellDef="let command">
              <app-status-chip [status]="command.status" [text]="'printers.commandStatus.' + command.status | translate" />
            </td>
          </ng-container>
          <ng-container matColumnDef="result">
            <th mat-header-cell *matHeaderCellDef>{{ 'printers.colResult' | translate }}</th>
            <td mat-cell *matCellDef="let command">{{ command.result }}</td>
          </ng-container>
          <ng-container matColumnDef="sent">
            <th mat-header-cell *matHeaderCellDef>{{ 'printers.colSent' | translate }}</th>
            <td mat-cell *matCellDef="let command" class="neo-nowrap">{{ command.createdAt | dateTime }}</td>
          </ng-container>
          <ng-container matColumnDef="completed">
            <th mat-header-cell *matHeaderCellDef>{{ 'printers.colCompleted' | translate }}</th>
            <td mat-cell *matCellDef="let command" class="neo-nowrap">{{ command.completedAt | dateTime }}</td>
          </ng-container>

          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
        </table>
      </div>
    }
  `,
  styles: `
    h3 {
      margin: var(--space-6) 0 var(--space-2);
    }

    .commands__danger:not(:disabled) {
      color: var(--danger-500);
    }

    .commands__actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-3);
      margin-block: var(--space-4);
    }
  `,
})
export class KioskCommandsComponent implements OnInit {
  private readonly admin = inject(AdminApiService);
  private readonly api = inject(MonitoringApiService);
  private readonly auth = inject(AuthService);
  private readonly hub = inject(AdminHubService);
  private readonly statuses = inject(KioskLiveStore);
  private readonly notify = inject(NotifyService);
  private readonly dialog = inject(MatDialog);
  private readonly translate = inject(TranslateService);
  private readonly results = commandResults();

  readonly kioskId = input.required<string>();

  readonly columns = ['type', 'status', 'result', 'sent', 'completed'];

  readonly commands = signal<KioskCommand[] | null>(null);
  readonly problem = signal<Problem | null>(null);
  readonly busy = signal<KioskCommandPath | null>(null);

  readonly canPause = computed(() => this.auth.can(Permissions.kioskCommandsPause));
  readonly canRestart = computed(() => this.auth.can(Permissions.kioskCommandsRestart));
  readonly canReboot = computed(() => this.auth.can(Permissions.kioskCommandsReboot));

  /** Paused by an administrator: "Davam et" instead of "Dayandır". The status every page shows. */
  readonly paused = computed(() => this.statuses.statusOf(this.kioskId()) === ('Maintenance' as KioskStatus));

  constructor() {
    const off = this.hub.on<KioskCommand>(HubEvents.commandResult, (command) => {
      if (command.kioskId === this.kioskId()) {
        this.commands.update((list) => [command, ...(list ?? []).filter((item) => item.id !== command.id)]);
      }
    });
    inject(DestroyRef).onDestroy(off);

    // Answers sent while the connection was away.
    liveRefresh({ events: [], reload: () => this.load(true) });
  }

  ngOnInit(): void {
    void this.load();
  }

  async load(quiet = false): Promise<void> {
    if (!quiet) {
      this.problem.set(null);
    }

    try {
      this.commands.set(await firstValueFrom(this.admin.kioskCommands(this.kioskId())));
    } catch (error) {
      if (!quiet) {
        this.problem.set(toProblem(error));
      }
    }
  }

  /** Pause, restart and reboot are asked about first: each stops the kiosk serving for a while. */
  async ask(command: Exclude<KioskCommandPath, 'resume'>): Promise<void> {
    const name = { pause: 'pause', 'restart-app': 'restart', reboot: 'reboot' }[command];
    const text = (key: string): string => this.translate.instant(key) as string;

    const confirmed = await confirmAction(this.dialog, {
      title: text(`monitoring.commands.${name}Title`),
      explanation: text(`monitoring.commands.${name}Explanation`),
      confirmLabel: text(`monitoring.commands.${name}`),
      cancelLabel: text('common.cancel'),
    });

    if (confirmed !== undefined) {
      await this.send(command);
    }
  }

  async send(command: KioskCommandPath): Promise<void> {
    this.busy.set(command);

    try {
      const sent = await firstValueFrom(this.api.command(this.kioskId(), command));

      // Pause and resume change the kiosk's status at once, on the server.
      if (command === 'pause') {
        this.statuses.learn(this.kioskId(), 'Maintenance' as KioskStatus);
      }

      this.commands.update((list) => (list?.some((item) => item.id === sent.id) ? list : [sent, ...(list ?? [])]));
      this.notify.success('printers.sent');
      this.results.sent(sent);
    } catch (error) {
      this.notify.error(toProblem(error).message);
    } finally {
      this.busy.set(null);
    }
  }
}
