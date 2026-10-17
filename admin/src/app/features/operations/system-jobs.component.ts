import { NgTemplateOutlet } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { OperationsApiService } from '../../core/api/operations-api.service';
import { JobRunSummary, SystemJobName, SystemJobs } from '../../core/api/step7.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { HubEvents } from '../../core/realtime/hub-events';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { BytesPipe, DateTimePipe, MoneyPipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent, StatusTone } from '../../shared/status-chip.component';

/** How a job's card is headed: its state as a chip. */
interface JobState {
  readonly tone: StatusTone;
  readonly icon: string;
  readonly text: string;
}

/** The kinds the money check reports, in the order the card lists them. */
const moneyKinds = ['paidNotPrinted', 'refundWithoutLedger', 'walletPaymentWithoutLedger', 'paidWithoutFiscal'] as const;

/** How many objects a dry run lists. */
const dryRunShown = 20;

/**
 * "Arxa plan işləri" (plan section 6; docs/DESIGN_SYSTEM.md, T-215): what the
 * system's own checks did last - the dead-letter queues, the file cleanup,
 * the balance and payment checks - and the kiosk events not kept. Nothing
 * here changes money: what the checks find is an incident. A SuperAdmin opens
 * the Hangfire panel from here.
 */
@Component({
  selector: 'app-system-jobs',
  imports: [
    NgTemplateOutlet,
    MatButtonModule,
    MatIconModule,
    RouterLink,
    TranslatePipe,
    PageHeaderComponent,
    ErrorBannerComponent,
    StatusChipComponent,
    DateTimePipe,
    BytesPipe,
    MoneyPipe,
  ],
  template: `
    <div class="neo-page">
      <div class="jobs__head">
        <app-page-header [title]="'bgjobs.title' | translate" [description]="'bgjobs.description' | translate" />
        @if (superAdmin()) {
          <button mat-stroked-button type="button" class="jobs__hangfire" (click)="openHangfire()">
            <mat-icon>open_in_new</mat-icon>{{ 'bgjobs.hangfire' | translate }}
          </button>
        }
      </div>

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="bgjobs.loadFailedEffect" (retry)="load()" />
      }

      @if (view(); as v) {
        <div class="jobs__grid">
          <!-- Dead-letter queues -->
          <section class="neo-card jobs__card" data-job="dead-letters" [class.jobs__card--bad]="bad('dead-letters')">
            <ng-container *ngTemplateOutlet="head; context: { $implicit: 'dead-letters' }"></ng-container>
            @if (v.runs['dead-letters']; as run) {
              @if (!run.succeeded) {
                <div class="neo-notice">
                  <mat-icon>cloud_off</mat-icon>
                  <span>{{ 'bgjobs.deadLetters.unreachable' | translate }}</span>
                </div>
              } @else if ((run.queues ?? []).every(isEmptyQueue)) {
                <p class="jobs__ok"><mat-icon>check_circle</mat-icon>{{ 'bgjobs.deadLetters.allEmpty' | translate }}</p>
              } @else {
                <table class="jobs__table">
                  <thead>
                    <tr>
                      <th>{{ 'bgjobs.deadLetters.queue' | translate }}</th>
                      <th class="jobs__number">{{ 'bgjobs.deadLetters.messages' | translate }}</th>
                      <th>{{ 'bgjobs.deadLetters.oldest' | translate }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (q of nonEmpty(run); track q.name) {
                      <tr [attr.data-queue]="q.name">
                        <td><code>{{ q.name }}</code></td>
                        <td class="jobs__number">{{ q.messages }}</td>
                        <td>
                          @if (q.oldestAt) {
                            {{ age(q.oldestAt) }}
                          } @else {
                            <span class="neo-caption">{{ 'bgjobs.deadLetters.ageUnknown' | translate }}</span>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
            }
          </section>

          <!-- File cleanup -->
          <section class="neo-card jobs__card" data-job="file-cleanup" [class.jobs__card--bad]="bad('file-cleanup')">
            <ng-container *ngTemplateOutlet="head; context: { $implicit: 'file-cleanup' }"></ng-container>
            @if (v.runs['file-cleanup']; as run) {
              @if (run.dryRun) {
                <div class="neo-notice neo-notice--warning">
                  <mat-icon>science</mat-icon>
                  <span>{{ 'bgjobs.files.dryRunNotice' | translate }}</span>
                </div>
              }
              <dl>
                <div class="neo-metric">
                  <dt>{{ 'bgjobs.files.examined' | translate }}</dt>
                  <dd>{{ run.counts['examined'] ?? 0 }}</dd>
                </div>
                <div class="neo-metric" data-metric="deleted">
                  <dt>{{ (run.dryRun ? 'bgjobs.files.wouldDelete' : 'bgjobs.files.deleted') | translate }}</dt>
                  <dd>{{ run.counts['deleted'] ?? 0 }}</dd>
                </div>
                <div class="neo-metric">
                  <dt>{{ 'bgjobs.files.freed' | translate }}</dt>
                  <dd>{{ run.counts['deletedBytes'] ?? 0 | bytes }}</dd>
                </div>
                <div class="neo-metric">
                  <dt>{{ 'bgjobs.files.protected' | translate }}</dt>
                  <dd>{{ run.counts['protected'] ?? 0 }}</dd>
                </div>
                <div class="neo-metric">
                  <dt>{{ 'bgjobs.files.recent' | translate }}</dt>
                  <dd>{{ run.counts['recent'] ?? 0 }}</dd>
                </div>
              </dl>
              @if (run.dryRun && run.findings.length > 0) {
                <h3 class="jobs__sub-title">{{ 'bgjobs.files.wouldDeleteList' | translate }}</h3>
                <ul class="jobs__list">
                  @for (f of run.findings.slice(0, dryRunShown); track f.ref) {
                    <li><code>{{ f.ref }}</code> <span class="neo-caption">{{ f.amount ?? 0 | bytes }}</span></li>
                  }
                </ul>
              }
            }
          </section>

          <!-- Wallet ledger -->
          <section class="neo-card jobs__card" data-job="wallet-ledger" [class.jobs__card--bad]="bad('wallet-ledger')">
            <ng-container *ngTemplateOutlet="head; context: { $implicit: 'wallet-ledger' }"></ng-container>
            @if (v.runs['wallet-ledger']; as run) {
              <dl>
                <div class="neo-metric">
                  <dt>{{ 'bgjobs.wallet.checked' | translate }}</dt>
                  <dd>{{ run.counts['checked'] ?? 0 }}</dd>
                </div>
                <div class="neo-metric">
                  <dt>{{ 'bgjobs.wallet.mismatches' | translate }}</dt>
                  <dd>{{ run.counts['mismatches'] ?? 0 }}</dd>
                </div>
              </dl>
              @if (run.findings.length > 0) {
                <table class="jobs__table">
                  <thead>
                    <tr>
                      <th>{{ 'bgjobs.wallet.account' | translate }}</th>
                      <th class="jobs__number">{{ 'bgjobs.wallet.balance' | translate }}</th>
                      <th class="jobs__number">{{ 'bgjobs.wallet.ledger' | translate }}</th>
                      <th class="jobs__number">{{ 'bgjobs.wallet.difference' | translate }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (f of run.findings; track f.ref) {
                      <tr [attr.data-account]="f.ref">
                        <td>
                          @if (canSeeUsers()) {
                            <a [routerLink]="['/customers', f.ref]">{{ f.label ?? f.ref }}</a>
                          } @else {
                            {{ f.label ?? f.ref }}
                          }
                        </td>
                        <td class="jobs__number">{{ f.amount ?? 0 | money }}</td>
                        <td class="jobs__number">{{ f.other ?? 0 | money }}</td>
                        <td class="jobs__number jobs__danger">{{ (f.amount ?? 0) - (f.other ?? 0) | money }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
            }
          </section>

          <!-- Payments against refunds, ledger and receipts -->
          <section class="neo-card jobs__card" data-job="payment-consistency" [class.jobs__card--bad]="bad('payment-consistency')">
            <ng-container *ngTemplateOutlet="head; context: { $implicit: 'payment-consistency' }"></ng-container>
            @if (v.runs['payment-consistency']; as run) {
              <dl>
                @for (kind of moneyKinds; track kind) {
                  <div class="neo-metric" [attr.data-metric]="kind">
                    <dt>{{ 'bgjobs.money.' + kind | translate }}</dt>
                    <dd>{{ run.counts[kind] ?? 0 }}</dd>
                  </div>
                }
              </dl>
              @if (run.findings.length > 0) {
                <h3 class="jobs__sub-title">{{ 'bgjobs.money.latest' | translate }}</h3>
                <ul class="jobs__list">
                  @for (f of run.findings; track f.kind + f.ref) {
                    <li>
                      {{ 'bgjobs.money.' + f.kind | translate }} ·
                      @if (canSeePayments()) {
                        <a [routerLink]="['/payments', f.ref]"><code>{{ f.ref.slice(0, 8) }}</code></a>
                      } @else {
                        <code>{{ f.ref.slice(0, 8) }}</code>
                      }
                      <span class="neo-caption">{{ f.amount ?? 0 | money }}</span>
                    </li>
                  }
                </ul>
              }
            }
          </section>

          <!-- Kiosk analytics -->
          <section class="neo-card jobs__card" data-job="analytics" [class.jobs__card--warn]="v.analytics.today > 0">
            <div class="jobs__card-head">
              <h2 class="jobs__card-title">{{ 'bgjobs.analytics.title' | translate }}</h2>
            </div>
            @if (v.analytics.today > 0) {
              <div class="neo-notice neo-notice--warning">
                <mat-icon>warning</mat-icon>
                <span>{{ 'bgjobs.analytics.notice' | translate }}</span>
              </div>
            }
            <dl>
              <div class="neo-metric">
                <dt>{{ 'bgjobs.analytics.today' | translate }}</dt>
                <dd>{{ v.analytics.today }}</dd>
              </div>
              <div class="neo-metric">
                <dt>{{ 'bgjobs.analytics.week' | translate }}</dt>
                <dd>{{ v.analytics.week }}</dd>
              </div>
            </dl>
            @if (v.analytics.kiosks.length > 0) {
              <table class="jobs__table">
                <thead>
                  <tr>
                    <th>{{ 'ops.kiosk' | translate }}</th>
                    <th class="jobs__number">{{ 'bgjobs.analytics.todayShort' | translate }}</th>
                    <th class="jobs__number">{{ 'bgjobs.analytics.weekShort' | translate }}</th>
                    <th>{{ 'bgjobs.analytics.lastUnknown' | translate }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (k of v.analytics.kiosks; track k.kioskId) {
                    <tr [attr.data-kiosk]="k.kioskCode ?? k.kioskId">
                      <td>{{ k.kioskCode ?? k.kioskId }}</td>
                      <td class="jobs__number">{{ k.today }}</td>
                      <td class="jobs__number">{{ k.week }}</td>
                      <td>
                        @if (k.lastUnknownName) {
                          <code>{{ k.lastUnknownName }}</code>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            }
          </section>
        </div>
      } @else if (!problem()) {
        <div class="neo-card">
          @for (row of [1, 2, 3]; track row) {
            <div class="neo-skeleton neo-skeleton--row"></div>
          }
        </div>
      }
    </div>

    <ng-template #head let-job>
      <div class="jobs__card-head">
        <h2 class="jobs__card-title">{{ 'bgjobs.name.' + job | translate }}</h2>
        @if (state(job); as s) {
          <app-status-chip [status]="job" [toneOverride]="s.tone" [iconOverride]="s.icon" [text]="s.text" />
        }
      </div>
      @if (run(job); as r) {
        <p class="neo-caption jobs__when">{{ 'bgjobs.lastRun' | translate: { time: (r.finishedAt | dateTime) } }}</p>
      } @else {
        <p class="neo-caption jobs__when">{{ 'bgjobs.neverRan' | translate }}</p>
      }
      @if (bad(job)) {
        <a class="jobs__incidents" routerLink="/incidents">{{ 'bgjobs.seeIncidents' | translate }}</a>
      }
    </ng-template>
  `,
  styleUrl: './system-jobs.scss',
})
export class SystemJobsComponent implements OnInit {
  private readonly api = inject(OperationsApiService);
  private readonly auth = inject(AuthService);
  private readonly translate = inject(TranslateService);
  private readonly notify = inject(NotifyService);

  readonly moneyKinds = moneyKinds;
  readonly dryRunShown = dryRunShown;

  readonly view = signal<SystemJobs | null>(null);
  readonly problem = signal<Problem | null>(null);

  readonly superAdmin = computed(() => this.auth.admin()?.roles.includes('SuperAdmin') ?? false);
  readonly canSeeUsers = computed(() => this.auth.can(Permissions.usersView));
  readonly canSeePayments = computed(() => this.auth.can(Permissions.paymentsView));

  constructor() {
    // A check that finds something opens an incident; one that clears closes it.
    liveRefresh({ events: [HubEvents.incidentRaised, HubEvents.incidentResolved], reload: () => this.load(true) });
  }

  ngOnInit(): void {
    void this.load();
  }

  async load(quiet = false): Promise<void> {
    try {
      this.view.set(await firstValueFrom(this.api.systemJobs()));
      this.problem.set(null);
    } catch (error) {
      if (!quiet) {
        this.problem.set(toProblem(error));
      }
    }
  }

  run(job: SystemJobName): JobRunSummary | undefined {
    return this.view()?.runs[job];
  }

  /** How many problems a job's last run found: messages waiting, mismatches. */
  problems(job: SystemJobName): number {
    const run = this.run(job);

    if (!run) {
      return 0;
    }

    switch (job) {
      case 'dead-letters':
        return run.counts['messages'] ?? 0;
      case 'wallet-ledger':
        return run.counts['mismatches'] ?? 0;
      case 'payment-consistency':
        return moneyKinds.reduce((sum, kind) => sum + (run.counts[kind] ?? 0), 0);
      default:
        return 0;
    }
  }

  bad(job: SystemJobName): boolean {
    return this.problems(job) > 0 || this.run(job)?.succeeded === false;
  }

  state(job: SystemJobName): JobState {
    const run = this.run(job);
    const t = (key: string, params?: Record<string, unknown>): string => this.translate.instant(key, params) as string;

    if (!run) {
      return { tone: 'neutral', icon: 'schedule', text: t('bgjobs.state.never') };
    }

    if (!run.succeeded) {
      return { tone: 'danger', icon: 'error', text: t('bgjobs.state.failed') };
    }

    const count = this.problems(job);

    if (count > 0) {
      return { tone: 'danger', icon: 'error', text: t(job === 'dead-letters' ? 'bgjobs.state.messages' : 'bgjobs.state.mismatches', { count }) };
    }

    if (run.dryRun) {
      return { tone: 'warning', icon: 'science', text: t('bgjobs.state.dryRun') };
    }

    return { tone: 'success', icon: 'check_circle', text: t('bgjobs.state.ok') };
  }

  nonEmpty(run: JobRunSummary): JobRunSummary['queues'] {
    return (run.queues ?? []).filter((q) => q.messages > 0);
  }

  isEmptyQueue(queue: { readonly messages: number }): boolean {
    return queue.messages === 0;
  }

  /** "3 saat əvvəl" - minutes under an hour, days over two. */
  age(at: string): string {
    const minutes = Math.max(0, Math.round((Date.now() - new Date(at).getTime()) / 60_000));

    if (minutes < 60) {
      return this.translate.instant('bgjobs.age.minutes', { count: minutes }) as string;
    }

    const hours = Math.round(minutes / 60);
    return hours < 48
      ? (this.translate.instant('bgjobs.age.hours', { count: hours }) as string)
      : (this.translate.instant('bgjobs.age.days', { count: Math.round(hours / 24) }) as string);
  }

  /** A ticket first (a cookie only the panel's path gets), then the panel in a tab of its own. */
  async openHangfire(): Promise<void> {
    const tab = window.open('', '_blank');

    try {
      await firstValueFrom(this.api.hangfireSession());

      if (tab) {
        tab.opener = null;
        tab.location.href = '/api/admin/hangfire';
      }
    } catch (error) {
      tab?.close();
      this.notify.error(toProblem(error).message || 'bgjobs.hangfireFailed');
    }
  }
}

