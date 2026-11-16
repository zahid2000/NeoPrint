import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TranslatePipe } from '@ngx-translate/core';

import { TierMode, VolumeTier } from '../../core/api/step2.models';

/** A ladder step as the form edits it (T-436): a percentage off, or prices of its own. */
export interface TierDraft {
  minSides: number;
  kind: 'Percent' | 'Price';
  discountPercent: number;
  bw: number;
  color: number;
}

export const tierFromApi = (tier: VolumeTier): TierDraft => {
  const priced = tier.bwPricePerSide != null || tier.colorPricePerSide != null;

  return {
    minSides: tier.minSides,
    kind: priced ? 'Price' : 'Percent',
    discountPercent: priced ? 0 : tier.discountPercent,
    bw: tier.bwPricePerSide ?? 0,
    color: tier.colorPricePerSide ?? 0,
  };
};

/** Only what the tier's kind uses goes to the API, ordered by where each starts. */
export const tiersToApi = (tiers: readonly TierDraft[]): VolumeTier[] =>
  [...tiers]
    .sort((a, b) => a.minSides - b.minSides)
    .map((tier) =>
      tier.kind === 'Price'
        ? {
            minSides: tier.minSides,
            discountPercent: 0,
            bwPricePerSide: tier.bw,
            colorPricePerSide: tier.color,
          }
        : { minSides: tier.minSides, discountPercent: tier.discountPercent },
    );

/** The rules the backend checks on a ladder (VolumeTier.Check), ahead of it - as translation keys. */
export const tierProblems = (tiers: readonly TierDraft[]): string[] => {
  const found: string[] = [];

  if (tiers.some((tier) => !(tier.minSides >= 1) || !Number.isInteger(tier.minSides))) {
    found.push('pricing.invalid.tierSides');
  }

  if (
    tiers.some(
      (tier) =>
        tier.kind === 'Percent' && !(tier.discountPercent > 0 && tier.discountPercent < 100),
    )
  ) {
    found.push('pricing.invalid.tierPercent');
  }

  if (tiers.some((tier) => tier.kind === 'Price' && !(tier.bw >= 0 && tier.color >= 0))) {
    found.push('pricing.invalid.tierPrices');
  }

  if (new Set(tiers.map((tier) => tier.minSides)).size !== tiers.length) {
    found.push('pricing.invalid.tierDuplicate');
  }

  return found;
};

/**
 * The volume ladder (docs/DESIGN_SYSTEM.md "Qiymətlər səhifəsi", T-436): how
 * it is read, and each step as a percentage off or a price of its own. The
 * profile form and a price campaign edit their ladders with it.
 */
@Component({
  selector: 'app-tier-ladder',
  imports: [
    FormsModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    MatTooltipModule,
    TranslatePipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="ladder__header">
      <h3>{{ 'pricing.tiers' | translate }}</h3>
      <button mat-button type="button" (click)="add()">
        <mat-icon>add</mat-icon>
        {{ 'pricing.addTier' | translate }}
      </button>
    </div>

    <div class="ladder__mode">
      <mat-button-toggle-group
        [attr.aria-label]="'pricing.tierModeLabel' | translate"
        [value]="mode()"
        (change)="modeChange.emit($event.value)"
      >
        <mat-button-toggle value="WholeOrder">{{
          'pricing.tierMode.WholeOrder' | translate
        }}</mat-button-toggle>
        <mat-button-toggle value="Graduated">{{
          'pricing.tierMode.Graduated' | translate
        }}</mat-button-toggle>
      </mat-button-toggle-group>
      <p class="neo-caption">{{ 'pricing.tierModeHint.' + mode() | translate }}</p>
    </div>

    @if (tiers().length === 0) {
      <p class="neo-muted neo-body-small">{{ 'pricing.noTiers' | translate }}</p>
    } @else {
      @for (tier of tiers(); track $index) {
        <div class="ladder__tier" data-tier>
          <mat-form-field>
            <mat-label>{{ 'pricing.tierFrom' | translate }}</mat-label>
            <input
              matInput
              type="number"
              min="1"
              step="1"
              [ngModel]="tier.minSides"
              (ngModelChange)="set($index, 'minSides', +$event)"
            />
            <span matTextSuffix>{{ 'pricing.sides' | translate }}</span>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'pricing.tierKindLabel' | translate }}</mat-label>
            <mat-select [ngModel]="tier.kind" (ngModelChange)="set($index, 'kind', $event)">
              <mat-option value="Percent">{{ 'pricing.tierKind.Percent' | translate }}</mat-option>
              <mat-option value="Price">{{ 'pricing.tierKind.Price' | translate }}</mat-option>
            </mat-select>
          </mat-form-field>
          @if (tier.kind === 'Percent') {
            <mat-form-field>
              <mat-label>{{ 'pricing.tierDiscount' | translate }}</mat-label>
              <input
                matInput
                type="number"
                min="0.1"
                max="99.9"
                step="0.5"
                [ngModel]="tier.discountPercent"
                (ngModelChange)="set($index, 'discountPercent', +$event)"
              />
              <span matTextSuffix>%</span>
            </mat-form-field>
          } @else {
            <mat-form-field>
              <mat-label>{{ 'pricing.bw' | translate }}</mat-label>
              <input
                matInput
                type="number"
                min="0"
                step="0.01"
                [ngModel]="tier.bw"
                (ngModelChange)="set($index, 'bw', +$event)"
              />
              <span matTextSuffix>₼</span>
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'pricing.color' | translate }}</mat-label>
              <input
                matInput
                type="number"
                min="0"
                step="0.01"
                [ngModel]="tier.color"
                (ngModelChange)="set($index, 'color', +$event)"
              />
              <span matTextSuffix>₼</span>
            </mat-form-field>
          }
          <button
            mat-icon-button
            type="button"
            [attr.aria-label]="'pricing.removeTier' | translate"
            [matTooltip]="'pricing.removeTier' | translate"
            (click)="remove($index)"
          >
            <mat-icon>delete</mat-icon>
          </button>
        </div>
      }
      <p class="neo-caption">{{ 'pricing.tiersHint' | translate }}</p>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
    }

    .ladder__header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-2);
    }

    .ladder__header h3 {
      margin: 0;
    }

    .ladder__mode {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
    }

    .ladder__mode p {
      margin: 0;
    }

    .ladder__tier {
      display: flex;
      flex-wrap: wrap;
      align-items: flex-start;
      gap: var(--space-2);
    }

    .ladder__tier mat-form-field {
      flex: 1 1 140px;
      min-width: 0;
    }
  `,
})
export class TierLadderComponent {
  readonly tiers = input.required<readonly TierDraft[]>();
  readonly mode = input.required<TierMode>();

  readonly tiersChange = output<TierDraft[]>();
  readonly modeChange = output<TierMode>();

  /** The next step starts past the last one; a price step until changed. */
  add(): void {
    const tiers = this.tiers();
    const next = tiers.length === 0 ? 51 : Math.max(...tiers.map((tier) => tier.minSides)) + 50;

    this.tiersChange.emit([
      ...tiers,
      { minSides: next, kind: 'Price', discountPercent: 5, bw: 0, color: 0 },
    ]);
  }

  set<K extends keyof TierDraft>(index: number, key: K, value: TierDraft[K]): void {
    this.tiersChange.emit(
      this.tiers().map((tier, i) => (i === index ? { ...tier, [key]: value } : tier)),
    );
  }

  remove(index: number): void {
    this.tiersChange.emit(this.tiers().filter((_, i) => i !== index));
  }
}
