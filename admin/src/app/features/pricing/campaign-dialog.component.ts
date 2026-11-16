import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, KioskListItem } from '../../core/api/admin-api.service';
import {
  CampaignKind,
  PricingCampaign,
  PricingCampaignInput,
  TierMode,
} from '../../core/api/step2.models';
import { provideNeoDates } from '../../shared/date-input';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { Problem, toProblem } from '../../shared/problem';
import { endOfDay, startOfDay } from '../money/money-shared';
import {
  TierDraft,
  TierLadderComponent,
  tierFromApi,
  tierProblems,
  tiersToApi,
} from './tier-ladder.component';

export interface CampaignDialogData {
  readonly campaign: PricingCampaign | null;
  readonly kiosks: readonly KioskListItem[];
}

/**
 * A pricing campaign's terms (T-436; docs/DESIGN_SYSTEM.md "Qiymətlər
 * səhifəsi"): a percentage off or prices of its own, for a while or until
 * switched off, at every kiosk or the ones chosen.
 */
@Component({
  selector: 'app-campaign-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatDatepickerModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    MatSlideToggleModule,
    TranslatePipe,
    ErrorBannerComponent,
    TierLadderComponent,
  ],
  providers: [provideNeoDates()],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>
      {{ (data.campaign ? 'campaigns.editTitle' : 'campaigns.newTitle') | translate }}
    </h2>
    <mat-dialog-content class="neo-dialog campaign">
      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="campaigns.saveFailedEffect" />
      }

      <mat-form-field>
        <mat-label>{{ 'campaigns.name' | translate }}</mat-label>
        <input
          matInput
          required
          maxlength="80"
          [ngModel]="name()"
          (ngModelChange)="name.set($event)"
        />
        <mat-hint>{{ 'campaigns.nameHint' | translate }}</mat-hint>
      </mat-form-field>

      <mat-button-toggle-group
        [attr.aria-label]="'campaigns.kindLabel' | translate"
        [value]="kind()"
        (change)="kind.set($event.value)"
      >
        <mat-button-toggle value="Percent">{{
          'campaigns.kind.Percent' | translate
        }}</mat-button-toggle>
        <mat-button-toggle value="Price">{{
          'campaigns.kind.Price' | translate
        }}</mat-button-toggle>
      </mat-button-toggle-group>
      <p class="neo-caption">{{ 'campaigns.kindHint.' + kind() | translate }}</p>

      @if (kind() === 'Percent') {
        <mat-form-field>
          <mat-label>{{ 'campaigns.percent' | translate }}</mat-label>
          <input
            matInput
            type="number"
            min="0.1"
            max="99.9"
            step="0.5"
            [ngModel]="percent()"
            (ngModelChange)="percent.set(+$event)"
          />
          <span matTextSuffix>%</span>
        </mat-form-field>
      } @else {
        <div class="campaign__grid">
          <mat-form-field>
            <mat-label>{{ 'pricing.bw' | translate }}</mat-label>
            <input
              matInput
              type="number"
              min="0"
              step="0.01"
              [ngModel]="bw()"
              (ngModelChange)="bw.set(+$event)"
            />
            <span matTextSuffix>₼</span>
            <mat-hint>{{ 'pricing.perSide' | translate }}</mat-hint>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'pricing.color' | translate }}</mat-label>
            <input
              matInput
              type="number"
              min="0"
              step="0.01"
              [ngModel]="color()"
              (ngModelChange)="color.set(+$event)"
            />
            <span matTextSuffix>₼</span>
            <mat-hint>{{ 'pricing.perSide' | translate }}</mat-hint>
          </mat-form-field>
        </div>
        <app-tier-ladder
          [tiers]="tiers()"
          [mode]="tierMode()"
          (tiersChange)="tiers.set($event)"
          (modeChange)="tierMode.set($event)"
        />
      }

      <mat-form-field>
        <mat-label>{{ 'campaigns.period' | translate }}</mat-label>
        <mat-date-range-input [rangePicker]="period">
          <input
            matStartDate
            [placeholder]="'date.hint' | translate"
            [ngModel]="startsAt()"
            (ngModelChange)="startsAt.set($event)"
          />
          <input
            matEndDate
            [placeholder]="'date.hint' | translate"
            [ngModel]="endsAt()"
            (ngModelChange)="endsAt.set($event)"
          />
        </mat-date-range-input>
        <mat-datepicker-toggle matIconSuffix [for]="period" />
        <mat-date-range-picker #period />
        <mat-hint>{{ 'campaigns.periodHint' | translate }}</mat-hint>
      </mat-form-field>

      <mat-form-field>
        <mat-label>{{ 'campaigns.kiosks' | translate }}</mat-label>
        <mat-select multiple [ngModel]="kioskIds()" (ngModelChange)="kioskIds.set($event)">
          @for (k of data.kiosks; track k.id) {
            <mat-option [value]="k.id">{{ k.code }} — {{ k.name }}</mat-option>
          }
        </mat-select>
        <mat-hint>{{ 'campaigns.kiosksHint' | translate }}</mat-hint>
      </mat-form-field>

      <mat-slide-toggle [ngModel]="isActive()" (ngModelChange)="isActive.set($event)">
        {{ 'campaigns.active' | translate }}
      </mat-slide-toggle>
      <p class="neo-caption">{{ 'campaigns.oneAtATime' | translate }}</p>

      @for (key of problems(); track key) {
        <p class="neo-field-error" role="alert">
          <mat-icon aria-hidden="true">error</mat-icon>
          {{ key | translate }}
        </p>
      }
    </mat-dialog-content>

    <mat-dialog-actions class="neo-dialog__actions">
      <button mat-button type="button" [disabled]="busy()" (click)="ref.close(false)">
        {{ 'common.cancel' | translate }}
      </button>
      <button
        mat-flat-button
        type="button"
        [disabled]="problems().length > 0 || busy()"
        (click)="save()"
      >
        {{ 'common.save' | translate }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .campaign {
      display: flex;
      flex-direction: column;
      gap: var(--space-3);
    }

    .campaign p {
      margin: 0;
    }

    .campaign__grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
      gap: var(--space-2);
    }
  `,
})
export class CampaignDialogComponent {
  readonly data = inject<CampaignDialogData>(MAT_DIALOG_DATA);
  readonly ref = inject<MatDialogRef<CampaignDialogComponent, boolean>>(MatDialogRef);
  private readonly api = inject(AdminApiService);

  private readonly campaign = this.data.campaign;

  readonly name = signal(this.campaign?.name ?? '');
  readonly kind = signal<CampaignKind>(this.campaign?.kind ?? 'Percent');
  readonly percent = signal(this.campaign?.percent || 10);
  readonly bw = signal(this.campaign?.bwPricePerSide ?? 0.1);
  readonly color = signal(this.campaign?.colorPricePerSide ?? 0.3);
  readonly tiers = signal<TierDraft[]>((this.campaign?.volumeTiers ?? []).map(tierFromApi));
  readonly tierMode = signal<TierMode>(this.campaign?.tierMode ?? 'WholeOrder');
  readonly startsAt = signal<Date | null>(
    this.campaign?.startsAt ? new Date(this.campaign.startsAt) : null,
  );
  readonly endsAt = signal<Date | null>(
    this.campaign?.endsAt ? new Date(this.campaign.endsAt) : null,
  );
  readonly kioskIds = signal<string[]>([...(this.campaign?.kioskIds ?? [])]);
  readonly isActive = signal(this.campaign?.isActive ?? true);

  readonly busy = signal(false);
  readonly problem = signal<Problem | null>(null);

  /** The rules the backend checks (PricingCampaign.Validate), ahead of it. */
  readonly problems = computed<string[]>(() => {
    const found: string[] = [];

    if (!this.name().trim()) {
      found.push('campaigns.invalid.name');
    }

    if (this.kind() === 'Percent' && !(this.percent() > 0 && this.percent() < 100)) {
      found.push('campaigns.invalid.percent');
    }

    if (this.kind() === 'Price') {
      if (!(this.bw() >= 0 && this.color() >= 0)) {
        found.push('pricing.invalid.price');
      }

      found.push(...tierProblems(this.tiers()));
    }

    const from = this.startsAt();
    const until = this.endsAt();

    if (from && until && until < from) {
      found.push('campaigns.invalid.dates');
    }

    return found;
  });

  async save(): Promise<void> {
    const price = this.kind() === 'Price';
    const input: PricingCampaignInput = {
      name: this.name().trim(),
      kind: this.kind(),
      percent: price ? 0 : this.percent(),
      bwPricePerSide: price ? this.bw() : null,
      colorPricePerSide: price ? this.color() : null,
      volumeTiers: price ? tiersToApi(this.tiers()) : [],
      tierMode: price ? this.tierMode() : 'WholeOrder',
      startsAt: startOfDay(this.startsAt()),
      endsAt: endOfDay(this.endsAt()),
      kioskIds: this.kioskIds(),
      isActive: this.isActive(),
    };

    this.busy.set(true);
    this.problem.set(null);

    try {
      await firstValueFrom(
        this.campaign
          ? this.api.updatePricingCampaign(this.campaign.id, input)
          : this.api.createPricingCampaign(input),
      );
      this.ref.close(true);
    } catch (error) {
      this.problem.set(toProblem(error));
      this.busy.set(false);
    }
  }
}
