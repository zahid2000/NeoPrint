import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { RouterLink } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import {
  KioskUpdateState,
  KioskVersion,
  Release,
  ReleaseChannel,
  ReleasesApiService,
  ReleasesPage,
  Rollout,
  RolloutAction,
} from '../../core/api/releases-api.service';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { HubEvents } from '../../core/realtime/hub-events';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { confirmAction } from '../../shared/confirm.dialog';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { openRolloutDialog, openUploadDialog } from './release-dialogs';
import { Chip, updateChips } from './update-chips';


const rolloutChips: Record<Rollout['status'], Chip> = {
  Active: { tone: 'success', icon: 'play_arrow' },
  Paused: { tone: 'warning', icon: 'pause' },
  Completed: { tone: 'neutral', icon: 'task_alt' },
  RolledBack: { tone: 'danger', icon: 'undo' },
};


/** What can be done with a rollout in each state (backend ReleaseRollout.Transitions). */
const rolloutActions: Record<Rollout['status'], readonly RolloutAction[]> = {
  Active: ['pause', 'complete', 'rollback'],
  Paused: ['resume', 'complete', 'rollback'],
  Completed: [],
  RolledBack: [],
};

/**
 * "Buraxılışlar" (plan section 8A; docs/DESIGN_SYSTEM.md, T-317): the signed
 * packages, their rollouts and every kiosk's versions. The whole service's:
 * an account of some kiosks does not get here (T-311).
 */
@Component({
  selector: 'app-releases',
  imports: [
    FormsModule,
    MatButtonModule,
    MatIconModule,
    MatMenuModule,
    MatSelectModule,
    MatTableModule,
    MatTabsModule,
    RouterLink,
    TranslatePipe,
    PageHeaderComponent,
    ErrorBannerComponent,
    EmptyStateComponent,
    StatusChipComponent,
    DateTimePipe,
  ],
  template: `
    <div class="neo-page">
      <app-page-header [title]="'releases.title' | translate" [description]="'releases.description' | translate">
        @if (canManage()) {
          <button mat-flat-button type="button" data-action="upload" [disabled]="!page()" (click)="upload()">
            <mat-icon>upload</mat-icon>{{ 'releases.upload' | translate }}
          </button>
        }
      </app-page-header>

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="releases.loadFailedEffect" (retry)="load()" />
      }

      @if (page(); as p) {
        @if (p.trustedKeys === 0) {
          <div class="neo-notice neo-notice--warning" data-notice="no-keys">
            <mat-icon>warning</mat-icon>
            <span>
              {{ 'releases.keySet.noKeys' | translate }}
              <span class="neo-caption releases__block">{{ 'releases.keySet.noKeysHint' | translate }}</span>
            </span>
          </div>
        } @else {
          <div class="neo-notice" data-notice="key-set">
            <mat-icon>verified</mat-icon>
            <span>
              {{ 'releases.keySet.notice' | translate: { set: ('releases.keySet.name.' + p.keySet | translate) } }}
              <span class="neo-caption releases__block">{{ 'releases.keySet.hint' | translate }}</span>
            </span>
          </div>
        }

        <mat-tab-group animationDuration="150ms" [selectedIndex]="tab()" (selectedIndexChange)="tab.set($event)">
          <mat-tab [label]="'releases.tab.releases' | translate">
            <section class="releases__tab" data-tab="releases">
              @if (p.releases.length === 0) {
                <app-empty-state icon="deployed_code" [message]="'releases.empty' | translate" [hint]="'releases.emptyHint' | translate" />
              } @else {
                <div class="neo-table-wrapper">
                  <table mat-table class="neo-table" [dataSource]="p.releases">
                    <ng-container matColumnDef="component">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.component' | translate }}</th>
                      <td mat-cell *matCellDef="let r">{{ 'releases.component.' + r.component | translate }}</td>
                    </ng-container>
                    <ng-container matColumnDef="platform">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.platform' | translate }}</th>
                      <td mat-cell *matCellDef="let r">{{ 'releases.platform.' + r.platform | translate }}</td>
                    </ng-container>
                    <ng-container matColumnDef="version">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.version' | translate }}</th>
                      <td mat-cell *matCellDef="let r" class="neo-nowrap">{{ r.version }}</td>
                    </ng-container>
                    <ng-container matColumnDef="channel">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.channel' | translate }}</th>
                      <td mat-cell *matCellDef="let r" [attr.data-release]="r.id">
                        @if (r.isWithdrawn) {
                          <app-status-chip status="Withdrawn" toneOverride="danger" iconOverride="block" [text]="'releases.withdrawn' | translate" />
                          @if (r.withdrawReason) {
                            <span class="neo-row-note">{{ 'releases.withdrawnReason' | translate: { reason: r.withdrawReason } }}</span>
                          }
                        } @else {
                          <app-status-chip
                            [status]="r.channel"
                            [toneOverride]="r.channel === 'Stable' ? 'success' : 'info'"
                            [iconOverride]="r.channel === 'Stable' ? 'verified' : 'science'"
                            [text]="'releases.channel.' + r.channel | translate"
                          />
                        }
                      </td>
                    </ng-container>
                    <ng-container matColumnDef="signature">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.signature' | translate }}</th>
                      <td mat-cell *matCellDef="let r">
                        <app-status-chip status="Signed" toneOverride="success" iconOverride="verified" [text]="'releases.signed' | translate" />
                        <span class="neo-caption releases__block">{{ 'releases.keyId' | translate: { id: r.keyId } }}</span>
                      </td>
                    </ng-container>
                    <ng-container matColumnDef="installed">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.installed' | translate }}</th>
                      <td mat-cell *matCellDef="let r">{{ 'releases.installedOn' | translate: { count: r.installedOn } }}</td>
                    </ng-container>
                    <ng-container matColumnDef="uploaded">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.uploaded' | translate }}</th>
                      <td mat-cell *matCellDef="let r" class="neo-nowrap">{{ r.uploadedAt | dateTime }}</td>
                    </ng-container>
                    <ng-container matColumnDef="actions">
                      <th mat-header-cell *matHeaderCellDef><span class="cdk-visually-hidden">{{ 'releases.col.actions' | translate }}</span></th>
                      <td mat-cell *matCellDef="let r">
                        @if (canManage() && !r.isWithdrawn) {
                          <button mat-icon-button type="button" [matMenuTriggerFor]="releaseMenu" [attr.aria-label]="'releases.menu' | translate">
                            <mat-icon>more_vert</mat-icon>
                          </button>
                          <mat-menu #releaseMenu>
                            <button mat-menu-item type="button" data-action="rollout" (click)="rollout(r)">
                              <mat-icon>rocket_launch</mat-icon>{{ 'releases.action.rollout' | translate }}
                            </button>
                            @if (r.channel === 'Pilot') {
                              <button mat-menu-item type="button" data-action="promote" (click)="promote(r)">
                                <mat-icon>trending_up</mat-icon>{{ 'releases.action.promote' | translate }}
                              </button>
                            }
                            <button mat-menu-item type="button" data-action="withdraw" class="releases__danger" (click)="withdraw(r)">
                              <mat-icon>block</mat-icon>{{ 'releases.action.withdraw' | translate }}
                            </button>
                          </mat-menu>
                        }
                      </td>
                    </ng-container>

                    <tr mat-header-row *matHeaderRowDef="releaseColumns"></tr>
                    <tr mat-row *matRowDef="let row; columns: releaseColumns"></tr>
                  </table>
                </div>
              }
            </section>
          </mat-tab>

          <mat-tab [label]="'releases.tab.rollouts' | translate">
            <section class="releases__tab" data-tab="rollouts">
              @if (p.rollouts.length === 0) {
                <app-empty-state icon="rocket_launch" [message]="'releases.rolloutsEmpty' | translate" [hint]="'releases.rolloutsEmptyHint' | translate" />
              } @else {
                <div class="neo-table-wrapper">
                  <table mat-table class="neo-table" [dataSource]="p.rollouts">
                    <ng-container matColumnDef="release">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.release' | translate }}</th>
                      <td mat-cell *matCellDef="let o">
                        {{ 'releases.component.' + o.component | translate }} <span class="neo-nowrap">{{ o.version }}</span>
                        <span class="neo-caption releases__block">{{ 'releases.platform.' + o.platform | translate }}</span>
                      </td>
                    </ng-container>
                    <ng-container matColumnDef="target">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.target' | translate }}</th>
                      <td mat-cell *matCellDef="let o">{{ targetText(o) }}</td>
                    </ng-container>
                    <ng-container matColumnDef="status">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.status' | translate }}</th>
                      <td mat-cell *matCellDef="let o" [attr.data-rollout]="o.id">
                        <app-status-chip
                          [status]="o.status"
                          [toneOverride]="rolloutChip(o).tone"
                          [iconOverride]="rolloutChip(o).icon"
                          [text]="'releases.rolloutStatus.' + o.status | translate"
                        />
                        @if (o.urgent) {
                          <app-status-chip
                            status="urgent"
                            toneOverride="warning"
                            iconOverride="bolt"
                            [text]="'releases.urgentChip' | translate"
                          />
                        }
                      </td>
                    </ng-container>
                    <ng-container matColumnDef="reaches">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.progress' | translate }}</th>
                      <td mat-cell *matCellDef="let o" class="releases__progress" [attr.data-progress]="o.id">
                        <span>{{ 'releases.progress.of' | translate: { installed: o.progress?.installed ?? 0, reaches: o.reaches } }}</span>
                        <div
                          class="releases__bar"
                          role="progressbar"
                          aria-valuemin="0"
                          [attr.aria-valuemax]="o.reaches"
                          [attr.aria-valuenow]="o.progress?.installed ?? 0"
                          [attr.aria-label]="'releases.col.progress' | translate"
                        >
                          <span [style.width.%]="installedPercent(o)"></span>
                        </div>
                        @if (progressOf(o).length > 0) {
                          <div class="releases__counts">
                            @for (part of progressOf(o); track part.key) {
                              <app-status-chip
                                [status]="part.key"
                                [toneOverride]="part.chip.tone"
                                [iconOverride]="part.chip.icon"
                                [text]="'releases.progress.' + part.key | translate: { count: part.count }"
                              />
                            }
                          </div>
                        }
                      </td>
                    </ng-container>
                    <ng-container matColumnDef="started">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.started' | translate }}</th>
                      <td mat-cell *matCellDef="let o" class="neo-nowrap">{{ o.createdAt | dateTime }}</td>
                    </ng-container>
                    <ng-container matColumnDef="actions">
                      <th mat-header-cell *matHeaderCellDef><span class="cdk-visually-hidden">{{ 'releases.col.actions' | translate }}</span></th>
                      <td mat-cell *matCellDef="let o">
                        @if (canManage() && actionsOf(o).length > 0) {
                          <button mat-icon-button type="button" [matMenuTriggerFor]="rolloutMenu" [attr.aria-label]="'releases.menu' | translate">
                            <mat-icon>more_vert</mat-icon>
                          </button>
                          <mat-menu #rolloutMenu>
                            @for (action of actionsOf(o); track action) {
                              <button mat-menu-item type="button" [attr.data-action]="action" (click)="change(o, action)">
                                {{ 'releases.rolloutAction.' + action | translate }}
                              </button>
                            }
                          </mat-menu>
                        }
                      </td>
                    </ng-container>

                    <tr mat-header-row *matHeaderRowDef="rolloutColumns"></tr>
                    <tr mat-row *matRowDef="let row; columns: rolloutColumns"></tr>
                  </table>
                </div>
              }
            </section>
          </mat-tab>

          <mat-tab [label]="'releases.tab.kiosks' | translate">
            <section class="releases__tab" data-tab="kiosks">
              @if (kiosks().length === 0) {
                <app-empty-state icon="devices" [message]="'releases.kiosksEmpty' | translate" [hint]="'releases.kiosksEmptyHint' | translate" />
              } @else {
                <div class="neo-table-wrapper">
                  <table mat-table class="neo-table" [dataSource]="kiosks()">
                    <ng-container matColumnDef="kiosk">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.kiosk' | translate }}</th>
                      <td mat-cell *matCellDef="let k" [attr.data-kiosk]="k.kioskId">
                        <a [routerLink]="['/kiosks', k.kioskId]" class="neo-nowrap">{{ k.code }}</a>
                        <span class="neo-caption releases__block">{{ k.name }}</span>
                      </td>
                    </ng-container>
                    <ng-container matColumnDef="platform">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.platform' | translate }}</th>
                      <td mat-cell *matCellDef="let k">
                        @if (k.platform) {
                          {{ 'releases.platform.' + k.platform | translate }}
                        }
                      </td>
                    </ng-container>
                    <ng-container matColumnDef="channel">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.channel' | translate }}</th>
                      <td mat-cell *matCellDef="let k">
                        @if (canManage()) {
                          <mat-select
                            class="releases__channel"
                            [ngModel]="k.channel"
                            (ngModelChange)="setChannel(k, $event)"
                            [aria-label]="'releases.col.channel' | translate"
                          >
                            @for (channel of channels; track channel) {
                              <mat-option [value]="channel">{{ 'releases.channel.' + channel | translate }}</mat-option>
                            }
                          </mat-select>
                        } @else {
                          {{ 'releases.channel.' + k.channel | translate }}
                        }
                      </td>
                    </ng-container>
                    <ng-container matColumnDef="app">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.app' | translate }}</th>
                      <td mat-cell *matCellDef="let k" class="neo-nowrap">{{ k.appVersion }}</td>
                    </ng-container>
                    <ng-container matColumnDef="agent">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.agent' | translate }}</th>
                      <td mat-cell *matCellDef="let k" class="neo-nowrap">{{ k.agentVersion }}</td>
                    </ng-container>
                    <ng-container matColumnDef="provisioning">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.provisioning' | translate }}</th>
                      <td mat-cell *matCellDef="let k" class="neo-nowrap">{{ k.provisioningVersion }}</td>
                    </ng-container>
                    <ng-container matColumnDef="offered">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.offered' | translate }}</th>
                      <td mat-cell *matCellDef="let k">
                        @for (o of k.offered; track o.component) {
                          <span class="releases__block neo-nowrap">
                            {{ 'releases.updateOf' | translate: { component: ('releases.component.' + o.component | translate), version: o.version } }}
                          </span>
                        }
                      </td>
                    </ng-container>
                    <ng-container matColumnDef="update">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.update' | translate }}</th>
                      <td mat-cell *matCellDef="let k">
                        @for (u of k.updates; track u.component) {
                          <div class="releases__update" [attr.data-update]="u.component">
                            <app-status-chip
                              [status]="u.status"
                              [toneOverride]="updateChip(u).tone"
                              [iconOverride]="updateChip(u).icon"
                              [text]="'releases.updateStatus.' + u.status | translate"
                            />
                            <span class="neo-caption">
                              {{ 'releases.updateOf' | translate: { component: ('releases.component.' + u.component | translate), version: u.version } }}
                            </span>
                            @if (u.message) {
                              <span class="neo-row-note" [class.neo-row-note--warning]="u.status === 'Failed' || u.status === 'RolledBack'">
                                @if (u.status === 'Failed' || u.status === 'RolledBack') {
                                  <mat-icon>warning</mat-icon>
                                }
                                {{ u.message }}
                              </span>
                            }
                          </div>
                        }
                      </td>
                    </ng-container>
                    <ng-container matColumnDef="lastSeen">
                      <th mat-header-cell *matHeaderCellDef>{{ 'releases.col.lastSeen' | translate }}</th>
                      <td mat-cell *matCellDef="let k" class="neo-nowrap">
                        @if (k.lastHeartbeatAt) {
                          {{ k.lastHeartbeatAt | dateTime }}
                        }
                      </td>
                    </ng-container>

                    <tr mat-header-row *matHeaderRowDef="kioskColumns"></tr>
                    <tr mat-row *matRowDef="let row; columns: kioskColumns"></tr>
                  </table>
                </div>
              }
            </section>
          </mat-tab>
        </mat-tab-group>
      } @else if (!problem()) {
        <div class="neo-skeleton neo-skeleton--block"></div>
      }
    </div>
  `,
  styleUrl: './releases.component.scss',
})
export class ReleasesComponent implements OnInit {
  private readonly api = inject(ReleasesApiService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly translate = inject(TranslateService);
  private readonly notify = inject(NotifyService);

  readonly releaseColumns = ['component', 'platform', 'version', 'channel', 'signature', 'installed', 'uploaded', 'actions'];
  readonly rolloutColumns = ['release', 'target', 'status', 'reaches', 'started', 'actions'];
  readonly kioskColumns = ['kiosk', 'platform', 'channel', 'app', 'agent', 'provisioning', 'offered', 'update', 'lastSeen'];
  readonly channels: readonly ReleaseChannel[] = ['Pilot', 'Stable'];

  /** The open tab: releases, rollouts, kiosk versions. */
  readonly tab = signal(0);
  readonly page = signal<ReleasesPage | null>(null);
  readonly kiosks = signal<KioskVersion[]>([]);
  readonly problem = signal<Problem | null>(null);

  readonly canManage = computed(() => this.auth.can(Permissions.releasesManage));

  constructor() {
    liveRefresh({ events: [HubEvents.releasesChanged, HubEvents.kioskUpdateReported], reload: () => this.load(true) });
  }

  ngOnInit(): void {
    void this.load();
  }

  async load(quiet = false): Promise<void> {
    try {
      const [page, kiosks] = await Promise.all([firstValueFrom(this.api.page()), firstValueFrom(this.api.versions())]);
      this.page.set(page);
      this.kiosks.set(kiosks);
      this.problem.set(null);
    } catch (error) {
      if (!quiet) {
        this.problem.set(toProblem(error));
      }
    }
  }

  rolloutChip(rollout: Rollout): Chip {
    return rolloutChips[rollout.status];
  }

  /** The kiosks a rollout reaches that run its version, of all it reaches. */
  installedPercent(rollout: Rollout): number {
    return rollout.reaches > 0 ? Math.min(100, ((rollout.progress?.installed ?? 0) * 100) / rollout.reaches) : 0;
  }

  /** The non-zero counts beside the bar (docs/DESIGN_SYSTEM.md, T-326). */
  progressOf(rollout: Rollout): readonly { key: string; count: number; chip: Chip }[] {
    const progress = rollout.progress;
    if (!progress) {
      return [];
    }

    const parts: { key: string; count: number; chip: Chip }[] = [
      { key: 'ready', count: progress.ready, chip: updateChips.Ready },
      { key: 'updating', count: progress.updating, chip: updateChips.Applying },
      { key: 'installed', count: progress.installed, chip: updateChips.Succeeded },
      { key: 'rolledBack', count: progress.rolledBack, chip: updateChips.RolledBack },
      { key: 'failed', count: progress.failed, chip: updateChips.Failed },
    ];
    return parts.filter((part) => part.count > 0);
  }

  updateChip(update: KioskUpdateState): Chip {
    return updateChips[update.status];
  }

  actionsOf(rollout: Rollout): readonly RolloutAction[] {
    return rolloutActions[rollout.status];
  }

  targetText(rollout: Rollout): string {
    return this.t('releases.targetText.' + rollout.target, {
      count: rollout.kioskIds.length,
      percent: rollout.percent,
      channel: rollout.channel ? this.t('releases.channel.' + rollout.channel) : '',
    });
  }

  async upload(): Promise<void> {
    const uploaded = await openUploadDialog(this.dialog);

    if (uploaded) {
      this.notify.success('releases.uploaded', this.named(uploaded));
      await this.load(true);
    }
  }

  async rollout(release: Release): Promise<void> {
    const created = await openRolloutDialog(this.dialog, { release, kiosks: this.kiosks() });

    if (created) {
      this.notify.success('releases.rolloutCreated', { count: created.reaches });
      await this.load(true);
    }
  }

  async promote(release: Release): Promise<void> {
    await this.act(async () => {
      await firstValueFrom(this.api.promote(release.id));
      this.notify.success('releases.promoted', this.named(release));
    });
  }

  async withdraw(release: Release): Promise<void> {
    const confirmed = await confirmAction(this.dialog, {
      title: this.t('releases.withdrawTitle', this.named(release)),
      explanation: this.t('releases.withdrawExplanation'),
      confirmLabel: this.t('releases.withdrawConfirm'),
      cancelLabel: this.t('common.cancel'),
      destructive: true,
      reasonLabel: this.t('releases.withdrawReason'),
    });

    if (!confirmed) {
      return;
    }

    await this.act(async () => {
      await firstValueFrom(this.api.withdraw(release.id, confirmed.reason));
      this.notify.success('releases.withdrawnDone', this.named(release));
    });
  }

  async change(rollout: Rollout, action: RolloutAction): Promise<void> {
    if (action === 'rollback') {
      const confirmed = await confirmAction(this.dialog, {
        title: this.t('releases.rollbackTitle'),
        explanation: this.t('releases.rollbackExplanation'),
        confirmLabel: this.t('releases.rolloutAction.rollback'),
        cancelLabel: this.t('common.cancel'),
      });

      if (!confirmed) {
        return;
      }
    }

    await this.act(() => firstValueFrom(this.api.changeRollout(rollout.id, action)));
  }

  async setChannel(kiosk: KioskVersion, channel: ReleaseChannel): Promise<void> {
    await this.act(async () => {
      await firstValueFrom(this.api.setChannel(kiosk.kioskId, channel));
      this.notify.success('releases.channelChanged', { kiosk: kiosk.code, channel: this.t('releases.channel.' + channel) });
    });
  }

  private async act(work: () => Promise<unknown>): Promise<void> {
    try {
      await work();
    } catch (error) {
      this.notify.error(toProblem(error).message || 'releases.actionFailed');
    }

    await this.load(true);
  }

  private named(release: Pick<Release, 'component' | 'version'>): Record<string, unknown> {
    return { component: this.t('releases.component.' + release.component), version: release.version };
  }

  private t(key: string, params?: Record<string, unknown>): string {
    return this.translate.instant(key, params) as string;
  }
}
