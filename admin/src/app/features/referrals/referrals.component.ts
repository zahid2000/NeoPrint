import { NgTemplateOutlet } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MoneyApiService } from '../../core/api/money-api.service';
import { ReferralFilter, ReferralPage, referralOutcome } from '../../core/api/step6.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { PagerComponent } from '../money/money-shared';

const pageSize = 20;

/** The history's tabs, in the order they are shown; null is "all". */
const tabs: readonly (ReferralFilter | null)[] = [null, 'Waiting', 'Rewarded', 'Held'];

/**
 * "Referral" (plan section 8A.3; docs/DESIGN_SYSTEM.md, "Admin: referral və
 * istifadəçinin faylları"): the programme in numbers, the top inviters and
 * the history of invitations. People are named by public id here; the email
 * is on the customer's own page, for those allowed to open it.
 */
@Component({
  selector: 'app-referrals',
  imports: [
    NgTemplateOutlet,
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
  ],
  template: `
    <div class="neo-page">
      <app-page-header
        [title]="'referrals.title' | translate"
        [description]="'referrals.description' | translate"
      />

      @if (problem(); as problem) {
        <app-error-banner
          [problem]="problem"
          effect="referrals.loadFailedEffect"
          (retry)="load()"
        />
      }

      @if (data(); as d) {
        <div class="money__stats">
          <div class="neo-card">
            <span class="neo-caption">{{ 'referrals.statRegistered' | translate }}</span>
            <span class="money__stat-value">{{ d.totals.registered }}</span>
          </div>
          <div class="neo-card">
            <span class="neo-caption">{{ 'referrals.statRewarded' | translate }}</span>
            <span class="money__stat-value">{{ d.totals.rewarded }}</span>
            <span class="neo-caption">{{
              'referrals.statBonusGiven' | translate: { pages: d.totals.bonusPagesGiven }
            }}</span>
          </div>
          <div class="neo-card">
            <span class="neo-caption">{{ 'referrals.statWaiting' | translate }}</span>
            <span class="money__stat-value">{{ d.totals.waiting }}</span>
          </div>
          <div class="neo-card">
            <span class="neo-caption">{{ 'referrals.statHeld' | translate }}</span>
            <span class="money__stat-value">{{ d.totals.held }}</span>
            <span class="neo-caption">{{ 'referrals.statHeldHint' | translate }}</span>
          </div>
        </div>

        <section class="neo-card">
          <h2 class="money__card-title">{{ 'referrals.topTitle' | translate }}</h2>
          @if (d.top.length === 0) {
            <p class="neo-muted">{{ 'referrals.topEmpty' | translate }}</p>
          } @else {
            <div class="neo-table-wrapper">
              <table mat-table class="neo-table" [dataSource]="d.top">
                <ng-container matColumnDef="user">
                  <th mat-header-cell *matHeaderCellDef>{{ 'referrals.colUser' | translate }}</th>
                  <td mat-cell *matCellDef="let t">
                    <ng-container
                      [ngTemplateOutlet]="party"
                      [ngTemplateOutletContext]="{ $implicit: t.inviter }"
                    />
                  </td>
                </ng-container>
                <ng-container matColumnDef="invited">
                  <th mat-header-cell *matHeaderCellDef class="money__number">
                    {{ 'referrals.colInvited' | translate }}
                  </th>
                  <td mat-cell *matCellDef="let t" class="money__number">{{ t.invited }}</td>
                </ng-container>
                <ng-container matColumnDef="rewarded">
                  <th mat-header-cell *matHeaderCellDef class="money__number">
                    {{ 'referrals.colRewarded' | translate }}
                  </th>
                  <td mat-cell *matCellDef="let t" class="money__number">{{ t.rewarded }}</td>
                </ng-container>
                <ng-container matColumnDef="bonus">
                  <th mat-header-cell *matHeaderCellDef class="money__number">
                    {{ 'referrals.colBonusPages' | translate }}
                  </th>
                  <td mat-cell *matCellDef="let t" class="money__number">{{ t.bonusPages }}</td>
                </ng-container>

                <tr mat-header-row *matHeaderRowDef="topColumns"></tr>
                <tr mat-row *matRowDef="let row; columns: topColumns"></tr>
              </table>
            </div>
          }
        </section>

        <h2 class="money__card-title referrals__history">
          {{ 'referrals.historyTitle' | translate }}
        </h2>
        <mat-tab-group [selectedIndex]="tabIndex()" (selectedIndexChange)="tab($event)">
          <mat-tab [label]="'referrals.tabAll' | translate" />
          <mat-tab [label]="'referrals.tabWaiting' | translate" />
          <mat-tab [label]="'referrals.tabRewarded' | translate" />
          <mat-tab [label]="'referrals.tabHeld' | translate" />
        </mat-tab-group>

        @if (d.items.length === 0) {
          <app-empty-state
            icon="card_giftcard"
            [message]="(filter() ? 'referrals.emptyFiltered' : 'referrals.empty') | translate"
            [hint]="filter() ? '' : ('referrals.emptyHint' | translate)"
          />
        } @else {
          <div class="neo-table-wrapper">
            <table mat-table class="neo-table" [dataSource]="d.items">
              <ng-container matColumnDef="date">
                <th mat-header-cell *matHeaderCellDef>{{ 'referrals.colDate' | translate }}</th>
                <td mat-cell *matCellDef="let r">{{ r.createdAt | dateTime }}</td>
              </ng-container>
              <ng-container matColumnDef="inviter">
                <th mat-header-cell *matHeaderCellDef>{{ 'referrals.colInviter' | translate }}</th>
                <td mat-cell *matCellDef="let r">
                  <ng-container
                    [ngTemplateOutlet]="party"
                    [ngTemplateOutletContext]="{ $implicit: r.inviter }"
                  />
                </td>
              </ng-container>
              <ng-container matColumnDef="invitee">
                <th mat-header-cell *matHeaderCellDef>{{ 'referrals.colInvitee' | translate }}</th>
                <td mat-cell *matCellDef="let r">
                  <ng-container
                    [ngTemplateOutlet]="party"
                    [ngTemplateOutletContext]="{ $implicit: r.invitee }"
                  />
                </td>
              </ng-container>
              <ng-container matColumnDef="outcome">
                <th mat-header-cell *matHeaderCellDef>{{ 'referrals.colOutcome' | translate }}</th>
                <td mat-cell *matCellDef="let r">
                  <app-status-chip
                    [status]="outcome(r)"
                    [text]="'referrals.outcome.' + outcome(r) | translate"
                  />
                  @if (r.hold !== 'None') {
                    <span class="neo-caption money__block">{{
                      'referrals.hold.' + r.hold | translate
                    }}</span>
                  } @else if (r.qualifiedAt) {
                    <span class="neo-caption money__block">{{
                      'referrals.decidedAt' | translate: { time: (r.qualifiedAt | dateTime) }
                    }}</span>
                  }
                </td>
              </ng-container>
              <ng-container matColumnDef="bonus">
                <th mat-header-cell *matHeaderCellDef>{{ 'referrals.colBonus' | translate }}</th>
                <td mat-cell *matCellDef="let r">
                  @if (r.status === 'Rewarded') {
                    {{
                      (r.inviteeBonusPages > 0 ? 'referrals.bonusBoth' : 'referrals.bonusInviter')
                        | translate: { inviter: r.inviterBonusPages, invitee: r.inviteeBonusPages }
                    }}
                  }
                </td>
              </ng-container>

              <tr mat-header-row *matHeaderRowDef="columns"></tr>
              <tr mat-row *matRowDef="let row; columns: columns"></tr>
            </table>
          </div>
          <app-money-pager
            [total]="d.total"
            [page]="page()"
            [pageSize]="pageSize"
            countKey="referrals.count"
            (go)="go($event)"
          />
        }
      } @else if (loading()) {
        <div class="neo-card">
          @for (row of [1, 2, 3]; track row) {
            <div class="neo-skeleton neo-skeleton--row"></div>
          }
        </div>
      }
    </div>

    <!-- A user by public id: a link to their page for those who may open it. -->
    <ng-template #party let-user>
      @if (!user.publicId) {
        <span class="neo-muted">{{ 'referrals.deletedUser' | translate }}</span>
      } @else if (canOpenCustomers()) {
        <a [routerLink]="['/customers', user.id]">{{ user.publicId }}</a>
      } @else {
        {{ user.publicId }}
      }
    </ng-template>
  `,
  styleUrl: '../money/money.scss',
  styles: `
    .referrals__history {
      margin-block-start: var(--space-6);
    }
  `,
})
export class ReferralsComponent implements OnInit {
  private readonly api = inject(MoneyApiService);
  private readonly auth = inject(AuthService);

  readonly pageSize = pageSize;
  readonly topColumns = ['user', 'invited', 'rewarded', 'bonus'];
  readonly columns = ['date', 'inviter', 'invitee', 'outcome', 'bonus'];

  readonly data = signal<ReferralPage | null>(null);
  readonly filter = signal<ReferralFilter | null>(null);
  readonly page = signal(1);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);

  readonly tabIndex = computed(() => tabs.indexOf(this.filter()));
  readonly canOpenCustomers = computed(() => this.auth.can(Permissions.usersView));

  readonly outcome = referralOutcome;

  ngOnInit(): void {
    void this.load();
  }

  tab(index: number): void {
    const next = tabs[index] ?? null;

    // The tab group reports the index it was just given too: the same tab is not loaded twice.
    if (next === this.filter()) {
      return;
    }

    this.filter.set(next);
    this.page.set(1);
    void this.load();
  }

  go(page: number): void {
    this.page.set(page);
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.problem.set(null);

    try {
      this.data.set(
        await firstValueFrom(this.api.listReferrals(this.filter(), this.page(), pageSize)),
      );
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }
}
