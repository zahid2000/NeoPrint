import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, KioskListItem } from '../../core/api/admin-api.service';
import { CampaignState, PricingCampaign } from '../../core/api/step2.models';
import { confirmAction } from '../../shared/confirm.dialog';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe, MoneyPipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent, StatusTone } from '../../shared/status-chip.component';
import { CampaignDialogComponent, CampaignDialogData } from './campaign-dialog.component';

/** The state chip's tone and icon (docs/DESIGN_SYSTEM.md "Qiymətlər səhifəsi"). */
const stateLook: Record<CampaignState, { tone: StatusTone; icon: string }> = {
  Running: { tone: 'success', icon: 'play_circle' },
  Scheduled: { tone: 'info', icon: 'schedule' },
  Ended: { tone: 'neutral', icon: 'event_busy' },
  Off: { tone: 'neutral', icon: 'pause_circle' },
};

/**
 * The "Kampaniyalar" tab of the pricing page (T-436): no code is typed - while
 * one runs, every order at its kiosks is priced with it; a promo code still
 * comes on top.
 */
@Component({
  selector: 'app-pricing-campaigns',
  imports: [
    MatButtonModule,
    MatIconModule,
    MatTableModule,
    MatTooltipModule,
    TranslatePipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    StatusChipComponent,
    DateTimePipe,
    MoneyPipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="campaigns__head">
      <p class="neo-muted neo-body-small">{{ 'campaigns.description' | translate }}</p>
      <button mat-flat-button type="button" (click)="edit(null)">
        <mat-icon>add</mat-icon>{{ 'campaigns.new' | translate }}
      </button>
    </div>

    @if (problem(); as problem) {
      <app-error-banner [problem]="problem" effect="campaigns.loadFailedEffect" (retry)="load()" />
    }

    @if (loading()) {
      <div class="neo-card">
        @for (row of [1, 2, 3]; track row) {
          <div class="neo-skeleton neo-skeleton--row"></div>
        }
      </div>
    } @else if (campaigns().length === 0 && !problem()) {
      <app-empty-state
        icon="campaign"
        [message]="'campaigns.empty' | translate"
        [hint]="'campaigns.emptyHint' | translate"
      />
    } @else if (campaigns().length > 0) {
      <div class="neo-table-wrapper">
        <table mat-table class="neo-table" [dataSource]="campaigns()">
          <ng-container matColumnDef="name">
            <th mat-header-cell *matHeaderCellDef>{{ 'campaigns.colName' | translate }}</th>
            <td mat-cell *matCellDef="let c">
              <strong>{{ c.name }}</strong>
            </td>
          </ng-container>
          <ng-container matColumnDef="value">
            <th mat-header-cell *matHeaderCellDef>{{ 'campaigns.colValue' | translate }}</th>
            <td mat-cell *matCellDef="let c" class="neo-nowrap">
              @if (c.kind === 'Percent') {
                −{{ c.percent }}%
              } @else {
                {{ c.bwPricePerSide | money }} / {{ c.colorPricePerSide | money }}
                @if (c.volumeTiers.length > 0) {
                  <span class="neo-caption campaigns__block">{{
                    'campaigns.tierCount' | translate: { count: c.volumeTiers.length }
                  }}</span>
                }
              }
            </td>
          </ng-container>
          <ng-container matColumnDef="period">
            <th mat-header-cell *matHeaderCellDef>{{ 'campaigns.colPeriod' | translate }}</th>
            <td mat-cell *matCellDef="let c">
              @if (c.startsAt || c.endsAt) {
                <span class="neo-nowrap">{{
                  c.startsAt ? (c.startsAt | dateTime) : ('campaigns.fromNow' | translate)
                }}</span>
                –
                <span class="neo-nowrap">{{
                  c.endsAt ? (c.endsAt | dateTime) : ('campaigns.noEnd' | translate)
                }}</span>
              } @else {
                {{ 'campaigns.always' | translate }}
              }
            </td>
          </ng-container>
          <ng-container matColumnDef="kiosks">
            <th mat-header-cell *matHeaderCellDef>{{ 'campaigns.colKiosks' | translate }}</th>
            <td mat-cell *matCellDef="let c" [attr.title]="kioskNames(c)">
              @if (c.kioskIds.length === 0) {
                {{ 'campaigns.allKiosks' | translate }}
              } @else {
                {{ 'campaigns.kioskCount' | translate: { count: c.kioskIds.length } }}
              }
            </td>
          </ng-container>
          <ng-container matColumnDef="state">
            <th mat-header-cell *matHeaderCellDef>{{ 'campaigns.colState' | translate }}</th>
            <td mat-cell *matCellDef="let c">
              <app-status-chip
                [status]="c.state"
                [text]="'campaigns.state.' + c.state | translate"
                [toneOverride]="look(c.state).tone"
                [iconOverride]="look(c.state).icon"
              />
            </td>
          </ng-container>
          <ng-container matColumnDef="actions">
            <th mat-header-cell *matHeaderCellDef>
              <span class="cdk-visually-hidden">{{ 'common.actions' | translate }}</span>
            </th>
            <td mat-cell *matCellDef="let c" class="campaigns__actions">
              <button
                mat-icon-button
                type="button"
                [attr.aria-label]="'common.edit' | translate"
                [matTooltip]="'common.edit' | translate"
                (click)="edit(c)"
              >
                <mat-icon>edit</mat-icon>
              </button>
              <button
                mat-icon-button
                type="button"
                [attr.aria-label]="'common.delete' | translate"
                [matTooltip]="'common.delete' | translate"
                (click)="remove(c)"
              >
                <mat-icon>delete</mat-icon>
              </button>
            </td>
          </ng-container>

          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
        </table>
      </div>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--space-4);
    }

    .campaigns__head {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-3);
    }

    .campaigns__head p {
      flex: 1 1 320px;
      margin: 0;
    }

    .campaigns__block {
      display: block;
    }

    .campaigns__actions {
      white-space: nowrap;
      text-align: right;
    }
  `,
})
export class PricingCampaignsComponent {
  private readonly api = inject(AdminApiService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly translate = inject(TranslateService);

  /** Every kiosk, for the dialog's choice and the column's names. */
  readonly kiosks = input<readonly KioskListItem[]>([]);

  readonly campaigns = signal<PricingCampaign[]>([]);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);

  readonly columns = ['name', 'value', 'period', 'kiosks', 'state', 'actions'];

  private readonly kioskById = computed(
    () => new Map(this.kiosks().map((kiosk) => [kiosk.id, kiosk])),
  );

  constructor() {
    void this.load();
  }

  look(state: CampaignState): { tone: StatusTone; icon: string } {
    return stateLook[state] ?? stateLook.Off;
  }

  kioskNames(campaign: PricingCampaign): string | null {
    if (campaign.kioskIds.length === 0) {
      return null;
    }

    return campaign.kioskIds.map((id) => this.kioskById().get(id)?.code ?? id).join(', ');
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.problem.set(null);

    try {
      this.campaigns.set(await firstValueFrom(this.api.listPricingCampaigns()));
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }

  async edit(campaign: PricingCampaign | null): Promise<void> {
    const saved = await firstValueFrom(
      this.dialog
        .open<CampaignDialogComponent, CampaignDialogData, boolean>(CampaignDialogComponent, {
          data: { campaign, kiosks: this.kiosks() },
          width: '640px',
          maxWidth: '95vw',
          autoFocus: 'first-tabbable',
        })
        .afterClosed(),
    );

    if (saved) {
      this.notify.success('campaigns.saved');
      await this.load();
    }
  }

  async remove(campaign: PricingCampaign): Promise<void> {
    const confirmed = await confirmAction(this.dialog, {
      title: this.translate.instant('campaigns.deleteTitle', { name: campaign.name }) as string,
      explanation: this.translate.instant('campaigns.deleteExplanation') as string,
      confirmLabel: this.translate.instant('common.delete') as string,
      cancelLabel: this.translate.instant('common.cancel') as string,
      destructive: true,
    });

    if (!confirmed) {
      return;
    }

    try {
      await firstValueFrom(this.api.deletePricingCampaign(campaign.id));
      this.notify.success('campaigns.deleted', { name: campaign.name });
      await this.load();
    } catch (error) {
      this.notify.error(toProblem(error).message);
    }
  }
}
