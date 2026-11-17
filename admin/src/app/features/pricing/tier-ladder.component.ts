import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TranslatePipe } from '@ngx-translate/core';

import { TierMode, VolumeTier } from '../../core/api/step2.models';
import { MoneyPipe } from '../../shared/format.pipes';

/** A ladder step as the form edits it (T-436): a percentage off, or prices of its own. */
export interface TierDraft {
  minSides: number;
  kind: 'Percent' | 'Price';
  discountPercent: number;
  bw: number;
  color: number;
}

export type LadderKind = TierDraft['kind'];

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

const bySides = (tiers: readonly TierDraft[]): TierDraft[] =>
  [...tiers].sort((a, b) => a.minSides - b.minSides);

/** Only what the tier's kind uses goes to the API, ordered by where each starts. */
export const tiersToApi = (tiers: readonly TierDraft[]): VolumeTier[] =>
  bySides(tiers).map((tier) =>
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

const cents = (amount: number): number => Math.round(amount * 100) / 100;

/** The ladder is one kind (T-442): a price ladder unless every step is a percentage. */
export const ladderKind = (tiers: readonly TierDraft[]): LadderKind =>
  tiers.length > 0 && tiers.every((tier) => tier.kind === 'Percent') ? 'Percent' : 'Price';

/** What a step charges a side, worked out from the base prices for a percentage step. */
export const sidePrices = (
  tier: TierDraft,
  baseBw: number,
  baseColor: number,
): { bw: number; color: number } =>
  tier.kind === 'Price'
    ? { bw: tier.bw, color: tier.color }
    : {
        bw: cents(baseBw * (1 - tier.discountPercent / 100)),
        color: cents(baseColor * (1 - tier.discountPercent / 100)),
      };

/**
 * Turns every step to `kind`, keeping what each charges: a percentage becomes
 * the side price it comes to, a price the percentage off the base it means.
 */
export const convertLadder = (
  tiers: readonly TierDraft[],
  kind: LadderKind,
  baseBw: number,
  baseColor: number,
): TierDraft[] =>
  tiers.map((tier) => {
    if (tier.kind === kind) {
      return tier;
    }

    if (kind === 'Price') {
      return { ...tier, kind, ...sidePrices(tier, baseBw, baseColor), discountPercent: 0 };
    }

    const off = baseBw > 0 ? Math.round((1 - tier.bw / baseBw) * 1000) / 10 : 5;

    return { ...tier, kind, discountPercent: Math.min(99.9, Math.max(0.1, off)), bw: 0, color: 0 };
  });

/** One row of the ladder as it reads: where it starts and ends, `to` null for "and up". */
export interface LadderRow {
  readonly index: number;
  readonly from: number;
  readonly to: number | null;
  readonly tier: TierDraft;
}

/**
 * Rows stay in the order they were entered - re-sorting while a number is typed
 * would move the field away from the cursor; the ladder is sorted when saved
 * and arrives sorted from the API.
 */
export const ladderRows = (tiers: readonly TierDraft[]): LadderRow[] => {
  const starts = [...new Set(tiers.map((tier) => tier.minSides))].sort((a, b) => a - b);

  return tiers.map((tier, index) => {
    const next = starts.find((start) => start > tier.minSides);

    return { index, from: tier.minSides, to: next === undefined ? null : next - 1, tier };
  });
};

/** The base row runs up to the first step: "1–50" for a ladder starting at 51. */
export const baseRowEnd = (tiers: readonly TierDraft[]): number =>
  Math.max(0, Math.min(...tiers.map((tier) => tier.minSides)) - 1);

/**
 * An illustration of the ladder on black and white A4 (T-442): `sides` priced
 * by `mode` as the backend's PriceCalculator does, without the minimum order.
 * The calculator on the page gives the exact figure.
 */
export const ladderExample = (
  tiers: readonly TierDraft[],
  mode: TierMode,
  baseBw: number,
  sides: number,
): number => {
  const steps = bySides(tiers).filter((tier) => tier.minSides <= sides);
  // Unrounded, as the backend works a percentage step: 15% off 0.10 is 0.085 a side.
  const rate = (tier: TierDraft | undefined) =>
    tier === undefined
      ? baseBw
      : tier.kind === 'Price'
        ? tier.bw
        : baseBw * (1 - tier.discountPercent / 100);

  if (mode === 'WholeOrder') {
    return cents(sides * rate(steps.at(-1)));
  }

  let total = 0;
  let from = 1;
  let current: TierDraft | undefined;

  for (const step of steps) {
    total += (step.minSides - from) * rate(current);
    from = step.minSides;
    current = step;
  }

  return cents(total + (sides - from + 1) * rate(current));
};

/**
 * The volume ladder (docs/DESIGN_SYSTEM.md "Qiymətlər səhifəsi", T-436, T-442):
 * one kind for the whole ladder, the base row it starts from, each step's range
 * spelled out, and how the steps add up with a worked example. The profile form
 * and a price campaign edit their ladders with it.
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
    MatTooltipModule,
    MoneyPipe,
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
    <p class="neo-caption">{{ 'pricing.tiersHint' | translate }}</p>

    @if (tiers().length === 0) {
      <p class="neo-muted neo-body-small">{{ 'pricing.noTiers' | translate }}</p>
    } @else {
      <div class="ladder__kind">
        <span class="ladder__label">{{ 'pricing.ladderKindLabel' | translate }}</span>
        <mat-button-toggle-group
          [attr.aria-label]="'pricing.ladderKindLabel' | translate"
          [value]="kind()"
          (change)="changeKind($event.value)"
        >
          <mat-button-toggle value="Price">{{
            'pricing.tierKind.Price' | translate
          }}</mat-button-toggle>
          <mat-button-toggle value="Percent">{{
            'pricing.tierKind.Percent' | translate
          }}</mat-button-toggle>
        </mat-button-toggle-group>
      </div>

      <div class="ladder__row ladder__row--base" data-base-row>
        <span class="ladder__range">{{
          'pricing.range.between' | translate: { from: 1, to: baseEnd() }
        }}</span>
        <span>
          {{ 'pricing.baseRow' | translate }}: {{ 'pricing.bw' | translate }}
          {{ baseBw() | money }}, {{ 'pricing.color' | translate }} {{ baseColor() | money }}
        </span>
      </div>

      @for (row of rows(); track row.index) {
        <div class="ladder__row" data-tier>
          <span class="ladder__range">
            @if (row.to === null) {
              {{ 'pricing.range.andUp' | translate: { from: row.from } }}
            } @else {
              {{ 'pricing.range.between' | translate: { from: row.from, to: row.to } }}
            }
          </span>
          <mat-form-field class="ladder__sides">
            <mat-label>{{ 'pricing.tierFrom' | translate }}</mat-label>
            <input
              matInput
              type="number"
              min="1"
              step="1"
              [ngModel]="row.tier.minSides"
              (ngModelChange)="set(row.index, 'minSides', +$event)"
            />
            <span matTextSuffix>{{ 'pricing.sides' | translate }}</span>
          </mat-form-field>
          @if (row.tier.kind === 'Price') {
            <mat-form-field>
              <mat-label>{{ 'pricing.bw' | translate }}</mat-label>
              <input
                matInput
                type="number"
                min="0"
                step="0.01"
                [ngModel]="row.tier.bw"
                (ngModelChange)="set(row.index, 'bw', +$event)"
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
                [ngModel]="row.tier.color"
                (ngModelChange)="set(row.index, 'color', +$event)"
              />
              <span matTextSuffix>₼</span>
              <mat-hint>{{ 'pricing.perSide' | translate }}</mat-hint>
            </mat-form-field>
          } @else {
            <mat-form-field>
              <mat-label>{{ 'pricing.tierDiscount' | translate }}</mat-label>
              <input
                matInput
                type="number"
                min="0.1"
                max="99.9"
                step="0.5"
                [ngModel]="row.tier.discountPercent"
                (ngModelChange)="set(row.index, 'discountPercent', +$event)"
              />
              <span matTextSuffix>%</span>
              <mat-hint>{{
                'pricing.comesTo'
                  | translate
                    : {
                        bw: (price(row.tier).bw | money),
                        color: (price(row.tier).color | money),
                      }
              }}</mat-hint>
            </mat-form-field>
          }
          <button
            mat-icon-button
            type="button"
            [attr.aria-label]="'pricing.removeTier' | translate"
            [matTooltip]="'pricing.removeTier' | translate"
            (click)="remove(row.index)"
          >
            <mat-icon>delete</mat-icon>
          </button>
        </div>
      }

      <div class="ladder__mode">
        <span class="ladder__label">{{ 'pricing.tierModeLabel' | translate }}</span>
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
        <p class="ladder__example" data-example>
          <mat-icon aria-hidden="true">calculate</mat-icon>
          {{
            'pricing.example'
              | translate: { sides: example().sides, total: (example().total | money) }
          }}
        </p>
      </div>
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

    .ladder__header h3,
    .ladder__mode p,
    :host > p {
      margin: 0;
    }

    .ladder__kind,
    .ladder__mode {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
    }

    .ladder__row {
      display: flex;
      flex-wrap: wrap;
      align-items: flex-start;
      gap: var(--space-2);
    }

    .ladder__row--base {
      align-items: center;
      padding: var(--space-2) var(--space-3);
      border-radius: var(--radius-md);
      background: var(--mat-sys-surface-container);
    }

    .ladder__range {
      flex: 0 0 140px;
      padding-top: var(--space-3);
      font-weight: var(--font-weight-semibold);
    }

    .ladder__row--base .ladder__range {
      padding-top: 0;
    }

    .ladder__row mat-form-field {
      flex: 1 1 120px;
      min-width: 0;
    }

    .ladder__label {
      font-weight: var(--font-weight-semibold);
    }

    .ladder__example {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      font-weight: var(--font-weight-semibold);
    }
  `,
})
export class TierLadderComponent {
  readonly tiers = input.required<readonly TierDraft[]>();
  readonly mode = input.required<TierMode>();
  /** The side prices the ladder starts from - the first row, and what a percentage comes off. */
  readonly baseBw = input(0);
  readonly baseColor = input(0);

  readonly tiersChange = output<TierDraft[]>();
  readonly modeChange = output<TierMode>();

  readonly kind = computed(() => ladderKind(this.tiers()));
  readonly rows = computed(() => ladderRows(this.tiers()));
  readonly baseEnd = computed(() => baseRowEnd(this.tiers()));

  /** Past the last step, so every step shows in the example: 120 for a 51/101 ladder. */
  readonly example = computed(() => {
    const sides = Math.max(...this.tiers().map((tier) => tier.minSides || 1)) + 19;

    return { sides, total: ladderExample(this.tiers(), this.mode(), this.baseBw(), sides) };
  });

  price(tier: TierDraft): { bw: number; color: number } {
    return sidePrices(tier, this.baseBw(), this.baseColor());
  }

  /** The next step starts 50 sides past the last one, at the prices of the step before it. */
  add(): void {
    const tiers = this.tiers();
    const last = bySides(tiers).at(-1);
    const kind = tiers.length === 0 ? 'Price' : this.kind();
    const step: TierDraft = last
      ? { ...last, minSides: last.minSides + 50, kind }
      : {
          minSides: 51,
          kind,
          discountPercent: 5,
          bw: this.baseBw(),
          color: this.baseColor(),
        };

    this.tiersChange.emit([...tiers, step]);
  }

  changeKind(kind: LadderKind): void {
    this.tiersChange.emit(convertLadder(this.tiers(), kind, this.baseBw(), this.baseColor()));
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
