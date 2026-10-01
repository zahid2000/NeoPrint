import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { KioskListItem } from '../../core/api/admin-api.service';
import { MoneyApiService } from '../../core/api/money-api.service';
import { PricingProfile } from '../../core/api/step2.models';
import { Promo, PromoInput } from '../../core/api/step4.models';
import { provideNeoDates } from '../../shared/date-input';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { Problem, toProblem } from '../../shared/problem';
import { endOfDay, startOfDay } from './money-shared';

export interface PromoDialogData {
  readonly promo: Promo | null;
  readonly kiosks: readonly KioskListItem[];
  readonly profiles: readonly PricingProfile[];
}

/**
 * A promo code's terms (plan section 2.7): kind and value, period, limits,
 * minimum, and the kiosks and price profiles it is for. The code itself is
 * set once - it is on receipts already.
 */
@Component({
  selector: 'app-promo-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatDatepickerModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    TranslatePipe,
    ErrorBannerComponent,
  ],
  providers: [provideNeoDates()],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>
      {{ (data.promo ? 'promos.editTitle' : 'promos.newTitle') | translate }}
    </h2>
    <mat-dialog-content class="neo-dialog">
      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="promos.saveFailedEffect" />
      }

      <div class="money__form">
        <mat-form-field>
          <mat-label>{{ 'promos.code' | translate }}</mat-label>
          <input
            matInput
            [disabled]="!!data.promo"
            [ngModel]="code()"
            (ngModelChange)="code.set($event.toUpperCase())"
          />
          <mat-hint>{{ 'promos.codeHint' | translate }}</mat-hint>
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'promos.kindLabel' | translate }}</mat-label>
          <mat-select [ngModel]="kind()" (ngModelChange)="kind.set($event)">
            <mat-option value="Percent">{{ 'promos.kind.Percent' | translate }}</mat-option>
            <mat-option value="Fixed">{{ 'promos.kind.Fixed' | translate }}</mat-option>
          </mat-select>
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{
            (kind() === 'Percent' ? 'promos.valuePercent' : 'promos.valueFixed') | translate
          }}</mat-label>
          <input
            matInput
            type="number"
            min="0.01"
            step="0.01"
            [ngModel]="value()"
            (ngModelChange)="value.set($event)"
          />
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'promos.period' | translate }}</mat-label>
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
          <mat-hint>{{ 'promos.periodHint' | translate }}</mat-hint>
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'promos.totalLimit' | translate }}</mat-label>
          <input
            matInput
            type="number"
            min="1"
            step="1"
            [ngModel]="totalLimit()"
            (ngModelChange)="totalLimit.set($event)"
          />
          <mat-hint>{{ 'promos.limitHint' | translate }}</mat-hint>
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'promos.perUserLimit' | translate }}</mat-label>
          <input
            matInput
            type="number"
            min="1"
            step="1"
            [ngModel]="perUserLimit()"
            (ngModelChange)="perUserLimit.set($event)"
          />
          <mat-hint>{{ 'promos.limitHint' | translate }}</mat-hint>
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'promos.minAmount' | translate }}</mat-label>
          <input
            matInput
            type="number"
            min="0"
            step="0.01"
            [ngModel]="minAmount()"
            (ngModelChange)="minAmount.set($event)"
          />
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'promos.kiosks' | translate }}</mat-label>
          <mat-select multiple [ngModel]="kioskIds()" (ngModelChange)="kioskIds.set($event)">
            @for (k of data.kiosks; track k.id) {
              <mat-option [value]="k.id">{{ k.code }} — {{ k.name }}</mat-option>
            }
          </mat-select>
          <mat-hint>{{ 'promos.allWhenEmpty' | translate }}</mat-hint>
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'promos.profiles' | translate }}</mat-label>
          <mat-select multiple [ngModel]="profileIds()" (ngModelChange)="profileIds.set($event)">
            @for (p of data.profiles; track p.id) {
              <mat-option [value]="p.id">{{ p.name }}</mat-option>
            }
          </mat-select>
          <mat-hint>{{ 'promos.allWhenEmpty' | translate }}</mat-hint>
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'promos.note' | translate }}</mat-label>
          <input
            matInput
            maxlength="200"
            [ngModel]="description()"
            (ngModelChange)="description.set($event)"
          />
        </mat-form-field>
      </div>
    </mat-dialog-content>

    <mat-dialog-actions class="neo-dialog__actions">
      <button mat-button type="button" [disabled]="busy()" (click)="ref.close(false)">
        {{ 'common.cancel' | translate }}
      </button>
      <button mat-flat-button type="button" [disabled]="!valid() || busy()" (click)="save()">
        {{ 'common.save' | translate }}
      </button>
    </mat-dialog-actions>
  `,
  styleUrl: './money.scss',
})
export class PromoDialogComponent {
  readonly data = inject<PromoDialogData>(MAT_DIALOG_DATA);
  readonly ref = inject<MatDialogRef<PromoDialogComponent, boolean>>(MatDialogRef);
  private readonly api = inject(MoneyApiService);

  private readonly promo = this.data.promo;

  readonly code = signal(this.promo?.code ?? '');
  readonly kind = signal<'Percent' | 'Fixed'>(this.promo?.kind ?? 'Percent');
  readonly value = signal<number | null>(this.promo?.value ?? null);
  readonly startsAt = signal<Date | null>(
    this.promo?.startsAt ? new Date(this.promo.startsAt) : null,
  );
  readonly endsAt = signal<Date | null>(this.promo?.endsAt ? new Date(this.promo.endsAt) : null);
  readonly totalLimit = signal<number | null>(this.promo?.totalLimit ?? null);
  readonly perUserLimit = signal<number | null>(this.promo?.perUserLimit ?? null);
  readonly minAmount = signal<number>(this.promo?.minAmount ?? 0);
  readonly kioskIds = signal<string[]>([...(this.promo?.kioskIds ?? [])]);
  readonly profileIds = signal<string[]>([...(this.promo?.profileIds ?? [])]);
  readonly description = signal(this.promo?.description ?? '');

  readonly busy = signal(false);
  readonly problem = signal<Problem | null>(null);

  readonly valid = computed(() => this.code().trim().length > 0 && Number(this.value() ?? 0) > 0);

  async save(): Promise<void> {
    const input: PromoInput = {
      code: this.code().trim(),
      kind: this.kind(),
      value: Number(this.value()),
      startsAt: startOfDay(this.startsAt()),
      endsAt: endOfDay(this.endsAt()),
      totalLimit: this.totalLimit() ? Number(this.totalLimit()) : null,
      perUserLimit: this.perUserLimit() ? Number(this.perUserLimit()) : null,
      minAmount: Number(this.minAmount() ?? 0),
      kioskIds: this.kioskIds(),
      profileIds: this.profileIds(),
      description: this.description().trim() || null,
    };

    this.busy.set(true);
    this.problem.set(null);

    try {
      await firstValueFrom(
        this.promo ? this.api.updatePromo(this.promo.id, input) : this.api.createPromo(input),
      );
      this.ref.close(true);
    } catch (error) {
      this.problem.set(toProblem(error));
      this.busy.set(false);
    }
  }
}
