import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, AdminKioskSession } from '../../core/api/admin-api.service';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { confirmAction } from '../../shared/confirm.dialog';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { Problem, toProblem } from '../../shared/problem';

/** How often the list is read again while it is open: the minutes left move. */
export const kioskSessionsRefreshMs = 30_000;

/**
 * Kiosk detail → Status → "Açıq sessiyalar" (T-362, docs/DESIGN_SYSTEM.md):
 * the phones joined to the kiosk, and closing one with a reason.
 */
@Component({
  selector: 'app-kiosk-sessions',
  imports: [MatButtonModule, MatIconModule, MatTableModule, RouterLink, TranslatePipe, EmptyStateComponent, ErrorBannerComponent, DateTimePipe],
  template: `
    <section class="sessions">
      <h3>{{ 'kioskSessions.title' | translate }}</h3>
      <p class="neo-muted neo-body-small">{{ 'kioskSessions.hint' | translate }}</p>

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="kioskSessions.loadFailedEffect" (retry)="load()" />
      }

      @if (loading()) {
        <div class="neo-skeleton neo-skeleton--block"></div>
      } @else if (sessions().length === 0) {
        <app-empty-state icon="smartphone" [message]="'kioskSessions.empty' | translate" [hint]="'kioskSessions.emptyHint' | translate" />
      } @else {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="sessions()">
            <ng-container matColumnDef="customer">
              <th mat-header-cell *matHeaderCellDef>{{ 'kioskSessions.colCustomer' | translate }}</th>
              <td mat-cell *matCellDef="let s">
                @if (canCustomers()) {
                  <a [routerLink]="['/customers', s.userId]">{{ s.userPublicId }}</a>
                } @else {
                  {{ s.userPublicId }}
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="opened">
              <th mat-header-cell *matHeaderCellDef>{{ 'kioskSessions.colOpened' | translate }}</th>
              <td mat-cell *matCellDef="let s" class="neo-nowrap">{{ s.openedAt | dateTime }}</td>
            </ng-container>
            <ng-container matColumnDef="ends">
              <th mat-header-cell *matHeaderCellDef>{{ 'kioskSessions.colEnds' | translate }}</th>
              <td mat-cell *matCellDef="let s" class="neo-nowrap">
                {{ s.expiresAt | dateTime }}
                <span class="neo-caption">{{ 'kioskSessions.minutesLeft' | translate: { minutes: minutesLeft(s) } }}</span>
              </td>
            </ng-container>
            <ng-container matColumnDef="actions">
              <th mat-header-cell *matHeaderCellDef></th>
              <td mat-cell *matCellDef="let s">
                @if (canClose()) {
                  <button mat-button type="button" [disabled]="busy() === s.id" (click)="close(s)" [attr.data-close]="s.id">
                    <mat-icon>logout</mat-icon>{{ 'kioskSessions.close' | translate }}
                  </button>
                }
              </td>
            </ng-container>

            <tr mat-header-row *matHeaderRowDef="columns"></tr>
            <tr mat-row *matRowDef="let row; columns: columns"></tr>
          </table>
        </div>
      }
    </section>
  `,
  styles: `
    .sessions {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      margin-top: var(--space-6);
    }

    .sessions h3 {
      margin: 0;
    }

    .neo-caption {
      display: block;
    }
  `,
})
export class KioskSessionsComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly translate = inject(TranslateService);

  readonly kioskId = input.required<string>();

  readonly columns = ['customer', 'opened', 'ends', 'actions'];

  readonly sessions = signal<readonly AdminKioskSession[]>([]);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);
  /** The session being closed. */
  readonly busy = signal<string | null>(null);

  readonly canClose = computed(() => this.auth.canAny([Permissions.kioskCommandsPause]));
  readonly canCustomers = computed(() => this.auth.canAny([Permissions.usersView]));

  constructor() {
    // Read again after a gap in the connection, and now and then for the minutes left.
    liveRefresh({ events: [], reload: () => this.load(true) });
    const timer = setInterval(() => void this.load(true), kioskSessionsRefreshMs);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  ngOnInit(): void {
    void this.load();
  }

  minutesLeft(session: AdminKioskSession): number {
    return Math.max(0, Math.ceil((Date.parse(session.expiresAt) - Date.now()) / 60_000));
  }

  async load(quiet = false): Promise<void> {
    if (!quiet) {
      this.problem.set(null);
    }

    try {
      this.sessions.set(await firstValueFrom(this.api.kioskSessions(this.kioskId())));
      this.problem.set(null);
    } catch (error) {
      if (!quiet) {
        this.problem.set(toProblem(error));
      }
    } finally {
      this.loading.set(false);
    }
  }

  async close(session: AdminKioskSession): Promise<void> {
    const t = (key: string): string => this.translate.instant(key) as string;
    const confirmed = await confirmAction(this.dialog, {
      title: t('kioskSessions.confirmTitle'),
      explanation: t('kioskSessions.confirmExplanation'),
      confirmLabel: t('kioskSessions.close'),
      cancelLabel: t('common.cancel'),
      requireReason: true,
      reasonLabel: t('kioskSessions.reason'),
      reasonHint: t('kioskSessions.reasonHint'),
      minReasonLength: 3,
      maxReasonLength: 500,
    });

    if (confirmed === undefined) {
      return;
    }

    this.busy.set(session.id);
    try {
      await firstValueFrom(this.api.closeKioskSession(this.kioskId(), session.id, confirmed.reason));
      this.notify.success('kioskSessions.closed');
    } catch (error) {
      // Already over (404) reads the same as closed: the list says what is left.
      if (!(error instanceof HttpErrorResponse && error.status === 404)) {
        this.notify.error(toProblem(error).message);
      }
    } finally {
      this.busy.set(null);
      await this.load(true);
    }
  }
}
