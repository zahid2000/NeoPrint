import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MoneyApiService } from '../../core/api/money-api.service';
import { SuspectedInvestigation, suspectedDecisions } from '../../core/api/step4.models';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { MoneyPipe } from '../../shared/format.pipes';
import { Problem, toProblem } from '../../shared/problem';

export interface DecisionDialogData {
  readonly jobId: string;
  readonly investigation: SuspectedInvestigation;
}

/**
 * The decision on a job whose result was unknown (plan sections 2.5, 8A.3):
 * full refund, partial refund, no refund (it printed), or print again. The
 * reason and the note are mandatory and audited.
 */
@Component({
  selector: 'app-decision-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatRadioModule,
    TranslatePipe,
    ErrorBannerComponent,
    MoneyPipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>
      {{ 'suspected.decideTitle' | translate: { code: data.investigation.detail.job.jobCode } }}
    </h2>
    <mat-dialog-content class="neo-dialog">
      <p class="neo-dialog__explanation">{{ 'suspected.decideExplanation' | translate }}</p>
      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="suspected.decideFailedEffect" />
      }

      <mat-radio-group
        class="money__radio"
        [ngModel]="decision()"
        (ngModelChange)="decision.set($event)"
      >
        @for (option of decisions; track option) {
          <mat-radio-button [value]="option">{{
            'suspected.decision.' + option | translate
          }}</mat-radio-button>
        }
      </mat-radio-group>

      @if (decision() === 'PartialRefund') {
        <mat-form-field>
          <mat-label>{{ 'refund.amount' | translate }}</mat-label>
          <input
            matInput
            type="number"
            min="0.01"
            step="0.01"
            [ngModel]="amount()"
            (ngModelChange)="amount.set($event)"
          />
          <mat-hint>{{
            'refund.amountHint' | translate: { max: (refundable() | money) }
          }}</mat-hint>
        </mat-form-field>
      }

      @if (refunding() && destinations().length > 1) {
        <p class="neo-caption">{{ 'refund.destination' | translate }}</p>
        <mat-radio-group
          class="money__radio"
          [ngModel]="destination()"
          (ngModelChange)="destination.set($event)"
        >
          @for (option of destinations(); track option) {
            <mat-radio-button [value]="option">{{
              'money.destination.' + option | translate
            }}</mat-radio-button>
          }
        </mat-radio-group>
      }

      <div class="money__form">
        <mat-form-field>
          <mat-label>{{ 'common.reason' | translate }}</mat-label>
          <input
            matInput
            maxlength="200"
            [ngModel]="reason()"
            (ngModelChange)="reason.set($event)"
          />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'suspected.note' | translate }}</mat-label>
          <textarea
            matInput
            rows="3"
            maxlength="1000"
            [ngModel]="note()"
            (ngModelChange)="note.set($event)"
          ></textarea>
          <mat-hint>{{ 'suspected.noteHint' | translate }}</mat-hint>
        </mat-form-field>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions class="neo-dialog__actions">
      <button mat-button type="button" [disabled]="busy()" (click)="ref.close(false)">
        {{ 'common.cancel' | translate }}
      </button>
      <button mat-flat-button type="button" [disabled]="!valid() || busy()" (click)="submit()">
        {{ 'suspected.decide' | translate }}
      </button>
    </mat-dialog-actions>
  `,
  styleUrl: './money.scss',
})
export class DecisionDialogComponent {
  readonly data = inject<DecisionDialogData>(MAT_DIALOG_DATA);
  readonly ref = inject<MatDialogRef<DecisionDialogComponent, boolean>>(MatDialogRef);
  private readonly api = inject(MoneyApiService);

  readonly decisions = suspectedDecisions;

  readonly decision = signal<(typeof suspectedDecisions)[number]>('FullRefund');
  readonly amount = signal<number | null>(null);
  readonly reason = signal('');
  readonly note = signal('');
  readonly busy = signal(false);
  readonly problem = signal<Problem | null>(null);

  readonly refundable = computed(() => this.data.investigation.payment?.refundable ?? 0);
  readonly destinations = computed(() => [
    ...(this.data.investigation.payment?.externalDestinations ?? []),
  ]);
  readonly destination = signal<string | null>(
    this.data.investigation.payment?.externalDestinations[0] ?? null,
  );
  readonly refunding = computed(
    () => this.decision() === 'FullRefund' || this.decision() === 'PartialRefund',
  );

  readonly valid = computed(() => {
    if (!this.reason().trim() || !this.note().trim()) {
      return false;
    }

    if (this.decision() === 'PartialRefund') {
      const amount = Number(this.amount() ?? 0);
      return amount > 0 && amount <= this.refundable();
    }

    return true;
  });

  async submit(): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);

    try {
      await firstValueFrom(
        this.api.decide(this.data.jobId, {
          decision: this.decision(),
          amount:
            this.decision() === 'PartialRefund'
              ? Math.round(Number(this.amount()) * 100) / 100
              : null,
          destination:
            this.refunding() && this.destinations().length > 1 ? this.destination() : null,
          reason: this.reason().trim(),
          note: this.note().trim(),
        }),
      );
      this.ref.close(true);
    } catch (error) {
      this.problem.set(toProblem(error));
      this.busy.set(false);
    }
  }
}
