import { Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService } from '../../core/api/admin-api.service';
import { PrintAttempt, PrintJobDetail, cancellableStatuses } from '../../core/api/step3.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { confirmAction } from '../../shared/confirm.dialog';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe, MoneyPipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';

/**
 * One order (plan section 8A.3): what was asked for, what it cost at the
 * time, every status it went through and every try at printing it - the
 * evidence an operator needs when a customer says nothing came out.
 */
@Component({
  selector: 'app-print-job-detail',
  imports: [
    RouterLink,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTableModule,
    TranslatePipe,
    PageHeaderComponent,
    ErrorBannerComponent,
    EmptyStateComponent,
    StatusChipComponent,
    DateTimePipe,
    MoneyPipe,
  ],
  template: `
    <div class="neo-page">
      <a mat-button routerLink="/print-jobs" class="detail__back">
        <mat-icon>arrow_back</mat-icon>
        {{ 'jobs.backToList' | translate }}
      </a>

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="jobs.loadOneFailedEffect" (retry)="load()" />
      }

      @if (loading()) {
        <div class="neo-skeleton neo-skeleton--block"></div>
      } @else if (detail(); as current) {
        <app-page-header
          [title]="'jobs.detailTitle' | translate: { code: current.job.jobCode }"
          [description]="current.job.fileName ?? ('jobs.noFile' | translate)"
        >
          <app-status-chip [status]="current.job.status" [text]="'jobs.status.' + current.job.status | translate" />
          @if (canCancel()) {
            <button mat-button type="button" class="detail__danger" [disabled]="cancelling()" (click)="cancel()">
              @if (cancelling()) {
                <mat-spinner diameter="18" class="neo-button-spinner" />
              } @else {
                <mat-icon>cancel</mat-icon>
              }
              {{ 'jobs.cancel' | translate }}
            </button>
          }
        </app-page-header>

        <div class="detail__grid">
          <section class="neo-card detail__card">
            <dl class="detail__facts">
              <dt>{{ 'jobs.kiosk' | translate }}</dt>
              <dd>
                @if (canOpenKiosk()) {
                  <a [routerLink]="['/kiosks', current.job.kioskId]">{{ current.kioskCode ?? current.job.kioskId }}</a>
                } @else {
                  {{ current.kioskCode ?? current.job.kioskId }}
                }
              </dd>
              <dt>{{ 'jobs.source' | translate }}</dt>
              <dd>{{ 'jobs.source.' + current.job.source | translate }}</dd>
              <dt>{{ 'jobs.colCreated' | translate }}</dt>
              <dd>{{ current.job.createdAt | dateTime }}</dd>
              @if (current.job.finishedAt) {
                <dt>{{ 'jobs.colFinished' | translate }}</dt>
                <dd>{{ current.job.finishedAt | dateTime }}</dd>
              }
            </dl>

            <h3>{{ 'jobs.optionsTitle' | translate }}</h3>
            <dl class="detail__facts">
              <dt>{{ 'jobs.pages' | translate }}</dt>
              <dd>
                @if (current.job.options.pageRange) {
                  {{ 'jobs.rangePages' | translate: { range: current.job.options.pageRange, count: current.job.selectedPages } }}
                } @else {
                  {{ 'jobs.allPages' | translate: { count: current.job.pageCount } }}
                }
              </dd>
              <dt>{{ 'jobs.copies' | translate }}</dt>
              <dd>{{ current.job.options.copies }}</dd>
              <dt>{{ 'jobs.color' | translate }}</dt>
              <dd>{{ (current.job.options.color ? 'jobs.colorOn' : 'jobs.colorOff') | translate }}</dd>
              <dt>{{ 'jobs.sides' | translate }}</dt>
              <dd>{{ (current.job.options.duplex ? 'jobs.duplexOn' : 'jobs.duplexOff') | translate }}</dd>
              <dt>{{ 'jobs.paper' | translate }}</dt>
              <dd>{{ current.job.options.paper }}</dd>
              <dt>{{ 'jobs.perSheet' | translate }}</dt>
              <dd>{{ current.job.options.pagesPerSheet }}</dd>
            </dl>
          </section>

          <section class="neo-card detail__card">
            <h3>{{ 'jobs.priceTitle' | translate }}</h3>
            <p class="detail__total">{{ current.job.price.total | money }}</p>
            @if (current.job.paymentSkipped) {
              <p class="neo-caption">{{ 'jobs.devPayment' | translate }}</p>
            }
            <dl class="detail__facts">
              <dt>{{ 'jobs.priceBase' | translate }}</dt>
              <dd>{{ current.job.price.basePrice | money }}</dd>
              @if (current.job.price.volumeDiscount > 0) {
                <dt>{{ 'jobs.priceDiscount' | translate: { percent: current.job.price.discountPercent } }}</dt>
                <dd>{{ -current.job.price.volumeDiscount | money }}</dd>
              }
              <dt>{{ 'jobs.priceProfile' | translate }}</dt>
              <dd>{{ current.job.price.profileName }}</dd>
              <dt>{{ 'jobs.sides' | translate }}</dt>
              <dd>{{ 'jobs.priceSides' | translate: { sides: current.job.price.sides, sheets: current.job.price.sheets } }}</dd>
            </dl>
            @if (current.job.price.minimumApplied) {
              <p class="neo-caption">{{ 'jobs.priceMinimum' | translate }}</p>
            }

            <h3>{{ 'jobs.printedTitle' | translate }}</h3>
            <p>{{ 'jobs.required' | translate: { sheets: current.job.requiredSheets } }}</p>
            <p>{{ 'jobs.printed' | translate: { sheets: current.job.printedSheets, sides: current.job.printedSides } }}</p>
            @if (current.job.errorReason) {
              <p class="neo-field-error">
                <mat-icon aria-hidden="true">error</mat-icon>
                {{ 'jobs.errorReason' | translate }}: {{ current.job.errorReason }}
              </p>
            }
            @if (current.job.cancelReason) {
              <p>{{ 'jobs.cancelReason' | translate }}: {{ current.job.cancelReason }}</p>
            }
          </section>
        </div>

        <h2>{{ 'jobs.attemptsTitle' | translate }}</h2>
        <p class="neo-muted neo-body-small">{{ 'jobs.attemptsHint' | translate }}</p>
        @if (attempts().length === 0) {
          <app-empty-state icon="print" [message]="'jobs.attemptsEmpty' | translate" />
        } @else {
          <div class="neo-table-wrapper">
            <table mat-table class="neo-table" [dataSource]="attempts()">
              <ng-container matColumnDef="number">
                <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colAttempt' | translate }}</th>
                <td mat-cell *matCellDef="let attempt">{{ attempt.number }}</td>
              </ng-container>
              <ng-container matColumnDef="status">
                <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colStatus' | translate }}</th>
                <td mat-cell *matCellDef="let attempt">
                  <app-status-chip [status]="attempt.status" [text]="'jobs.status.' + attempt.status | translate" />
                  @if (attempt.recovered) {
                    <span class="neo-caption detail__block">{{ 'jobs.recovered' | translate }}</span>
                  }
                  @if (attempt.error) {
                    <span class="neo-caption detail__block">{{ attempt.error }}</span>
                  }
                </td>
              </ng-container>
              <ng-container matColumnDef="printer">
                <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colPrinter' | translate }}</th>
                <td mat-cell *matCellDef="let attempt">{{ attempt.printer }}</td>
              </ng-container>
              <ng-container matColumnDef="spooler">
                <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colSpooler' | translate }}</th>
                <td mat-cell *matCellDef="let attempt">{{ attempt.spoolerJobId }}</td>
              </ng-container>
              <ng-container matColumnDef="started">
                <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colStarted' | translate }}</th>
                <td mat-cell *matCellDef="let attempt">
                  @if (attempt.startedAt) {
                    {{ attempt.startedAt | dateTime }}
                  }
                </td>
              </ng-container>
              <ng-container matColumnDef="counter">
                <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colCounter' | translate }}</th>
                <td mat-cell *matCellDef="let attempt">{{ counterText(attempt) }}</td>
              </ng-container>
              <ng-container matColumnDef="reported">
                <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colReported' | translate }}</th>
                <td mat-cell *matCellDef="let attempt">
                  {{ 'jobs.reported' | translate: { sheets: attempt.reportedSheets, sides: attempt.reportedSides } }}
                </td>
              </ng-container>

              <tr mat-header-row *matHeaderRowDef="attemptColumns"></tr>
              <tr mat-row *matRowDef="let row; columns: attemptColumns"></tr>
            </table>
          </div>
        }

        <h2>{{ 'jobs.historyTitle' | translate }}</h2>
        @if (current.history.length === 0) {
          <app-empty-state icon="history" [message]="'jobs.historyEmpty' | translate" />
        } @else {
          <div class="neo-table-wrapper">
            <table mat-table class="neo-table" [dataSource]="current.history">
              <ng-container matColumnDef="at">
                <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colAt' | translate }}</th>
                <td mat-cell *matCellDef="let step">{{ step.at | dateTime }}</td>
              </ng-container>
              <ng-container matColumnDef="from">
                <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colFrom' | translate }}</th>
                <td mat-cell *matCellDef="let step">{{ 'jobs.status.' + step.from | translate }}</td>
              </ng-container>
              <ng-container matColumnDef="to">
                <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colTo' | translate }}</th>
                <td mat-cell *matCellDef="let step">
                  <app-status-chip [status]="step.to" [text]="'jobs.status.' + step.to | translate" />
                </td>
              </ng-container>
              <ng-container matColumnDef="actor">
                <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colActor' | translate }}</th>
                <td mat-cell *matCellDef="let step">{{ 'jobs.actor.' + step.actorType | translate }}</td>
              </ng-container>
              <ng-container matColumnDef="reason">
                <th mat-header-cell *matHeaderCellDef>{{ 'jobs.colReason' | translate }}</th>
                <td mat-cell *matCellDef="let step">{{ step.reason }}</td>
              </ng-container>

              <tr mat-header-row *matHeaderRowDef="historyColumns"></tr>
              <tr mat-row *matRowDef="let row; columns: historyColumns"></tr>
            </table>
          </div>
        }
      }
    </div>
  `,
  styles: `
    h2 {
      margin: var(--space-6) 0 var(--space-2);
    }

    .detail__card h3 {
      margin: var(--space-4) 0 var(--space-2);
    }

    .detail__card h3:first-child {
      margin-top: 0;
    }

    .detail__back {
      align-self: flex-start;
      margin-block-end: var(--space-2);
    }

    .detail__danger {
      color: var(--danger-500);
    }

    .detail__grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
      gap: var(--space-4);
      margin-block-end: var(--space-6);
    }

    .detail__card {
      padding: var(--space-4);
    }

    .detail__facts {
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: var(--space-2) var(--space-4);
      margin: 0 0 var(--space-4);
    }

    .detail__facts dt {
      color: var(--neutral-600);
    }

    .detail__facts dd {
      margin: 0;
    }

    .detail__total {
      margin: 0;
      font-size: var(--font-size-h1);
      font-weight: var(--font-weight-semibold);
    }

    .detail__block {
      display: block;
    }
  `,
})
export class PrintJobDetailComponent {
  private readonly api = inject(AdminApiService);
  private readonly auth = inject(AuthService);
  private readonly hub = inject(AdminHubService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly translate = inject(TranslateService);

  /** From the route. */
  readonly id = input.required<string>();

  readonly attemptColumns = ['number', 'status', 'printer', 'spooler', 'started', 'counter', 'reported'];
  readonly historyColumns = ['at', 'from', 'to', 'actor', 'reason'];

  readonly detail = signal<PrintJobDetail | null>(null);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);
  readonly cancelling = signal(false);

  readonly attempts = computed(() => this.detail()?.attempts ?? []);

  readonly canOpenKiosk = computed(() => this.auth.can(Permissions.kiosksView));

  readonly canCancel = computed(() => {
    const status = this.detail()?.job.status;

    return !!status && cancellableStatuses.includes(status) && this.auth.can(Permissions.jobsCancel);
  });

  constructor() {
    effect(() => {
      this.id();
      void this.load();
    });

    // The job moves while it is watched: the kiosk's queue event says so.
    const off = this.hub.on<{ kioskId: string }>('QueueUpdated', (event) => {
      if (event.kioskId === this.detail()?.job.kioskId) {
        void this.load(false);
      }
    });
    inject(DestroyRef).onDestroy(off);

    // After a gap in the connection the job is asked for again.
    liveRefresh({ events: [], reload: () => this.load(false) });
  }

  async load(showSkeleton = true): Promise<void> {
    if (showSkeleton) {
      this.loading.set(true);
    }

    this.problem.set(null);

    try {
      this.detail.set(await firstValueFrom(this.api.getPrintJob(this.id())));
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }

  counterText(attempt: PrintAttempt): string {
    // == null: the API leaves out null fields, so a missing counter arrives as undefined.
    if (attempt.counterBefore == null || attempt.counterAfter == null) {
      return this.t('jobs.noCounter');
    }

    const unit = attempt.counterUnit ? ` ${this.t(`jobs.counterUnit.${attempt.counterUnit}`)}` : '';

    return this.t('jobs.counter', { before: attempt.counterBefore, after: attempt.counterAfter }) + unit;
  }

  async cancel(): Promise<void> {
    const job = this.detail()?.job;

    if (!job) {
      return;
    }

    const confirmed = await confirmAction(this.dialog, {
      title: this.t('jobs.cancelTitle', { code: job.jobCode }),
      explanation: this.t('jobs.cancelExplanation'),
      confirmLabel: this.t('jobs.cancel'),
      cancelLabel: this.t('common.close'),
      destructive: true,
      reasonLabel: this.t('jobs.cancelReason'),
    });

    if (!confirmed) {
      return;
    }

    this.cancelling.set(true);

    try {
      await firstValueFrom(this.api.cancelPrintJob(job.id, confirmed.reason));
      this.notify.success('jobs.cancelled', { code: job.jobCode });
      await this.load(false);
    } catch (error) {
      this.notify.error(toProblem(error).message);
    } finally {
      this.cancelling.set(false);
    }
  }

  private t(key: string, params?: Record<string, unknown>): string {
    return this.translate.instant(key, params) as string;
  }
}
