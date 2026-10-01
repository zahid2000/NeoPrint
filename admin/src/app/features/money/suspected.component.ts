import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { RouterLink } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MoneyApiService } from '../../core/api/money-api.service';
import { Claim, SuspectedInvestigation, SuspectedJob } from '../../core/api/step4.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { confirmAction } from '../../shared/confirm.dialog';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe, MoneyPipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { DecisionDialogComponent, DecisionDialogData } from './decision-dialog.component';
import { PagerComponent } from './money-shared';

/**
 * "Baxış gözləyən sifarişlər" (plan section 8A.3): jobs whose result is
 * unknown, the oldest first, with the investigation card and the decision;
 * and the customers' claims, answered or closed from here.
 */
@Component({
  selector: 'app-suspected',
  imports: [
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    MatTableModule,
    MatTabsModule,
    RouterLink,
    TranslatePipe,
    PageHeaderComponent,
    ErrorBannerComponent,
    EmptyStateComponent,
    StatusChipComponent,
    PagerComponent,
    DateTimePipe,
    MoneyPipe,
  ],
  template: `
    <div class="neo-page">
      <app-page-header
        [title]="'suspected.title' | translate"
        [description]="'suspected.description' | translate"
      />

      @if (problem(); as problem) {
        <app-error-banner
          [problem]="problem"
          effect="suspected.loadFailedEffect"
          (retry)="reload()"
        />
      }

      <mat-tab-group (selectedIndexChange)="tab.set($event)">
        <mat-tab [label]="'suspected.tabJobs' | translate">
          @if (jobs().length === 0 && !loading()) {
            <app-empty-state
              icon="task_alt"
              [message]="'suspected.empty' | translate"
              [hint]="'suspected.emptyHint' | translate"
            />
          } @else {
            <div class="neo-table-wrapper list">
              <table mat-table class="neo-table" [dataSource]="jobs()">
                <ng-container matColumnDef="code">
                  <th mat-header-cell *matHeaderCellDef>{{ 'suspected.colJob' | translate }}</th>
                  <td mat-cell *matCellDef="let j">
                    <strong>{{ j.jobCode }}</strong>
                    @if (j.overdue) {
                      <app-status-chip status="Failed" [text]="'suspected.overdue' | translate" />
                    }
                  </td>
                </ng-container>
                <ng-container matColumnDef="kiosk">
                  <th mat-header-cell *matHeaderCellDef>{{ 'suspected.colKiosk' | translate }}</th>
                  <td mat-cell *matCellDef="let j">
                    {{ j.kioskCode }}
                    @if (j.kioskLastHeartbeatAt) {
                      <span class="neo-caption money__block">{{
                        'suspected.lastSeen'
                          | translate: { time: (j.kioskLastHeartbeatAt | dateTime) }
                      }}</span>
                    }
                  </td>
                </ng-container>
                <ng-container matColumnDef="since">
                  <th mat-header-cell *matHeaderCellDef>{{ 'suspected.colSince' | translate }}</th>
                  <td mat-cell *matCellDef="let j">{{ j.suspectedAt | dateTime }}</td>
                </ng-container>
                <ng-container matColumnDef="claims">
                  <th mat-header-cell *matHeaderCellDef>{{ 'suspected.colClaims' | translate }}</th>
                  <td mat-cell *matCellDef="let j">{{ j.openClaims }}</td>
                </ng-container>
                <ng-container matColumnDef="amount">
                  <th mat-header-cell *matHeaderCellDef class="money__number">
                    {{ 'suspected.colAmount' | translate }}
                  </th>
                  <td mat-cell *matCellDef="let j" class="money__number">
                    {{ j.amount | money }}
                    @if (j.paymentMethod) {
                      <span class="neo-caption money__block">{{
                        'money.method.' + j.paymentMethod | translate
                      }}</span>
                    }
                  </td>
                </ng-container>

                <tr mat-header-row *matHeaderRowDef="columns"></tr>
                <tr
                  mat-row
                  class="neo-table__row--clickable"
                  tabindex="0"
                  *matRowDef="let row; columns: columns"
                  [class.selected]="row.jobId === selectedId()"
                  (click)="select(row)"
                  (keydown.enter)="select(row)"
                ></tr>
              </table>
            </div>
          }

          @if (investigation(); as inv) {
            <section class="neo-card investigation">
              <div class="money__head">
                <h2 class="money__card-title">
                  {{ 'suspected.investigation' | translate: { code: inv.detail.job.jobCode } }}
                </h2>
                @if (canDecide()) {
                  <button mat-flat-button type="button" (click)="decide(inv)">
                    {{ 'suspected.decide' | translate }}
                  </button>
                }
              </div>

              <div class="money__grid">
                <dl class="money__rows">
                  <dt>{{ 'suspected.file' | translate }}</dt>
                  <dd>{{ inv.detail.job.fileName ?? ('jobs.noFile' | translate) }}</dd>
                  <dt>{{ 'suspected.ordered' | translate }}</dt>
                  <dd>
                    {{ inv.detail.job.selectedPages }} × {{ inv.detail.job.options.copies }} ·
                    {{ inv.detail.job.options.paper }}
                  </dd>
                  <dt>{{ 'suspected.reported' | translate }}</dt>
                  <dd>
                    {{
                      'suspected.sides'
                        | translate
                          : {
                              sides: inv.detail.job.printedSides,
                              sheets: inv.detail.job.printedSheets,
                            }
                    }}
                  </dd>
                  <dt>{{ 'suspected.kioskStatus' | translate }}</dt>
                  <dd>
                    {{ inv.kioskStatus }} ·
                    {{ inv.kioskLastHeartbeatAt ? (inv.kioskLastHeartbeatAt | dateTime) : '' }}
                  </dd>
                </dl>
                @if (inv.payment; as p) {
                  <dl class="money__rows">
                    <dt>{{ 'suspected.payment' | translate }}</dt>
                    <dd>
                      <a [routerLink]="['/payments', p.id]">{{ p.amount | money }}</a>
                    </dd>
                    <dt>{{ 'paymentDetail.method' | translate }}</dt>
                    <dd>{{ 'money.method.' + p.method | translate }}</dd>
                    <dt>{{ 'paymentDetail.refundable' | translate }}</dt>
                    <dd>{{ p.refundable | money }}</dd>
                  </dl>
                }
              </div>

              <h3 class="neo-caption">{{ 'suspected.attempts' | translate }}</h3>
              @for (a of inv.detail.attempts ?? []; track a.number) {
                <p class="money__item">
                  #{{ a.number }} {{ a.printer }} ·
                  {{ a.startedAt ? (a.startedAt | dateTime) : '' }} ·
                  {{ counterText(a.counterBefore, a.counterAfter) }}
                </p>
              } @empty {
                <p class="neo-muted">{{ 'suspected.noAttempts' | translate }}</p>
              }

              <h3 class="neo-caption">{{ 'suspected.aroundKiosk' | translate }}</h3>
              @for (i of inv.kioskIncidents; track i.id) {
                <p class="money__item">
                  {{ i.createdAt | dateTime }} · {{ i.type }} · {{ i.details }}
                </p>
              }
              @for (j of inv.kioskJobs; track j.id) {
                <p class="money__item">
                  {{ j.createdAt | dateTime }} · {{ j.jobCode }} ·
                  {{ 'jobs.status.' + j.status | translate }}
                </p>
              }
              @if (inv.kioskIncidents.length === 0 && inv.kioskJobs.length === 0) {
                <p class="neo-muted">{{ 'suspected.nothingAround' | translate }}</p>
              }

              <h3 class="neo-caption">{{ 'suspected.claims' | translate }}</h3>
              @for (c of inv.claims; track c.id) {
                <p class="money__item">{{ c.createdAt | dateTime }} · {{ c.text }}</p>
              } @empty {
                <p class="neo-muted">{{ 'suspected.noClaims' | translate }}</p>
              }
            </section>
          }
        </mat-tab>

        <mat-tab [label]="'suspected.tabClaims' | translate">
          <div class="claims-filter">
            <mat-form-field>
              <mat-label>{{ 'claims.filterStatus' | translate }}</mat-label>
              <mat-select
                [ngModel]="claimStatus()"
                (ngModelChange)="claimStatus.set($event); claimPage.set(1); loadClaims()"
              >
                <mat-option [value]="null">{{ 'common.all' | translate }}</mat-option>
                @for (s of claimStatuses; track s) {
                  <mat-option [value]="s">{{ 'money.claimStatus.' + s | translate }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
          </div>
          @for (c of claims(); track c.id) {
            <div class="neo-card claim">
              <div class="money__item-head">
                <span>
                  {{ c.createdAt | dateTime }} · {{ 'money.claimChannel.' + c.channel | translate }}
                  @if (c.jobId) {
                    · <a [routerLink]="['/print-jobs', c.jobId]">{{ c.jobCode }}</a>
                  }
                </span>
                <app-status-chip
                  [status]="c.status"
                  [text]="'money.claimStatus.' + c.status | translate"
                />
              </div>
              <p>{{ c.text }}</p>
              @if (c.contact) {
                <p class="neo-caption">
                  {{ 'claims.contact' | translate: { contact: c.contact } }}
                </p>
              }
              @if (c.reply) {
                <p class="neo-caption">{{ 'claims.reply' | translate }}: {{ c.reply }}</p>
              }
              @if (c.status !== 'Closed' && canDecide()) {
                <div class="money__actions">
                  <button mat-stroked-button type="button" (click)="answer(c)">
                    {{ 'claims.answer' | translate }}
                  </button>
                  <button mat-button type="button" (click)="close(c)">
                    {{ 'claims.close' | translate }}
                  </button>
                </div>
              }
            </div>
          } @empty {
            <app-empty-state
              icon="support_agent"
              [message]="'claims.empty' | translate"
              [hint]="'claims.emptyHint' | translate"
            />
          }
          <app-money-pager
            [total]="claimTotal()"
            [page]="claimPage()"
            [pageSize]="claimPageSize"
            countKey="claims.count"
            (go)="claimPage.set($event); loadClaims()"
          />
        </mat-tab>
      </mat-tab-group>
    </div>
  `,
  styleUrl: './money.scss',
  styles: `
    .list,
    .investigation,
    .claims-filter {
      margin-block-start: var(--space-4);
    }

    .claim {
      margin-block-end: var(--space-3);
    }

    .selected {
      background: var(--brand-50);
    }
  `,
})
export class SuspectedComponent implements OnInit {
  private readonly api = inject(MoneyApiService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly translate = inject(TranslateService);

  readonly columns = ['code', 'kiosk', 'since', 'claims', 'amount'];
  readonly claimStatuses = ['Open', 'Answered', 'Closed'];
  readonly claimPageSize = 20;

  readonly tab = signal(0);
  readonly jobs = signal<readonly SuspectedJob[]>([]);
  readonly loading = signal(true);
  readonly selectedId = signal<string | null>(null);
  readonly investigation = signal<SuspectedInvestigation | null>(null);
  readonly problem = signal<Problem | null>(null);

  readonly claims = signal<readonly Claim[]>([]);
  readonly claimTotal = signal(0);
  readonly claimPage = signal(1);
  readonly claimStatus = signal<string | null>('Open');

  readonly canDecide = computed(() => this.auth.can(Permissions.paymentsRefund));

  ngOnInit(): void {
    void this.reload();
  }

  /** The printer's page counter before and after, or that it was not read (not a network printer). */
  counterText(before?: number, after?: number): string {
    return before === undefined || after === undefined
      ? (this.translate.instant('suspected.noCounter') as string)
      : (this.translate.instant('suspected.counter', { before, after }) as string);
  }

  async reload(): Promise<void> {
    await Promise.all([this.loadJobs(), this.loadClaims()]);
  }

  async loadJobs(): Promise<void> {
    this.loading.set(true);
    this.problem.set(null);

    try {
      this.jobs.set(await firstValueFrom(this.api.listSuspected()));
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }

  async loadClaims(): Promise<void> {
    try {
      const page = await firstValueFrom(
        this.api.listClaims(this.claimStatus(), this.claimPage(), this.claimPageSize),
      );
      this.claims.set(page.items);
      this.claimTotal.set(page.total);
    } catch (error) {
      this.problem.set(toProblem(error));
    }
  }

  async select(job: SuspectedJob): Promise<void> {
    this.selectedId.set(job.jobId);

    try {
      this.investigation.set(await firstValueFrom(this.api.investigation(job.jobId)));
    } catch (error) {
      this.problem.set(toProblem(error));
    }
  }

  async decide(investigation: SuspectedInvestigation): Promise<void> {
    const jobId = investigation.detail.job.id;
    const done = await firstValueFrom(
      this.dialog
        .open<DecisionDialogComponent, DecisionDialogData, boolean>(DecisionDialogComponent, {
          data: { jobId, investigation },
          autoFocus: 'dialog',
        })
        .afterClosed(),
    );

    if (done) {
      this.notify.success('suspected.decided');
      this.investigation.set(null);
      this.selectedId.set(null);
      await this.reload();
    }
  }

  async answer(claim: Claim): Promise<void> {
    const t = (key: string): string => this.translate.instant(key) as string;
    const result = await confirmAction(this.dialog, {
      title: t('claims.answerTitle'),
      explanation: claim.text,
      confirmLabel: t('claims.answer'),
      cancelLabel: t('common.cancel'),
      requireReason: true,
      reasonLabel: t('claims.reply'),
    });

    if (!result?.reason) {
      return;
    }

    try {
      await firstValueFrom(this.api.answerClaim(claim.id, result.reason));
      this.notify.success('claims.answered');
      await this.loadClaims();
    } catch (error) {
      this.problem.set(toProblem(error));
    }
  }

  async close(claim: Claim): Promise<void> {
    const t = (key: string): string => this.translate.instant(key) as string;
    const result = await confirmAction(this.dialog, {
      title: t('claims.closeTitle'),
      explanation: t('claims.closeExplanation'),
      confirmLabel: t('claims.close'),
      cancelLabel: t('common.cancel'),
    });

    if (!result) {
      return;
    }

    try {
      await firstValueFrom(this.api.closeClaim(claim.id));
      await this.loadClaims();
    } catch (error) {
      this.problem.set(toProblem(error));
    }
  }
}
