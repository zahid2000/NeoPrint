import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { MoneyApiService } from '../../core/api/money-api.service';
import { RefundOptions } from '../../core/api/step4.models';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { MoneyPipe } from '../../shared/format.pipes';
import { Problem, toProblem } from '../../shared/problem';

export interface RefundDialogData {
  readonly paymentId: string;
  readonly options: RefundOptions;
}

/**
 * "Ödənişi qaytar" (plan section 8A.3; docs/DESIGN_SYSTEM.md, "Qaytar"
 * dialoqu): full or part, only the destinations this payment allows, the job
 * cancelled with it when it still can be, and a mandatory reason.
 */
@Component({
  selector: 'app-refund-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatCheckboxModule,
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
    <h2 mat-dialog-title>{{ 'refund.title' | translate }}</h2>
    <mat-dialog-content class="neo-dialog">
      <p class="neo-dialog__explanation">{{ 'refund.explanation' | translate }}</p>

      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="refund.failedEffect" />
      }

      <mat-radio-group class="radio" [ngModel]="partial()" (ngModelChange)="partial.set($event)">
        <mat-radio-button [value]="false">
          {{ 'refund.full' | translate: { amount: (data.options.refundable | money) } }}
        </mat-radio-button>
        <mat-radio-button [value]="true">{{ 'refund.partial' | translate }}</mat-radio-button>
      </mat-radio-group>

      @if (partial()) {
        <mat-form-field>
          <mat-label>{{ 'refund.amount' | translate }}</mat-label>
          <input
            matInput
            type="number"
            min="0.01"
            [max]="data.options.refundable"
            step="0.01"
            [ngModel]="amount()"
            (ngModelChange)="amount.set($event)"
          />
          <mat-hint>{{
            'refund.amountHint' | translate: { max: (data.options.refundable | money) }
          }}</mat-hint>
        </mat-form-field>
      }

      @if (destinations().length > 1) {
        <p class="neo-caption">{{ 'refund.destination' | translate }}</p>
        <mat-radio-group
          class="radio"
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

      @if (data.options.jobCancellable) {
        <mat-checkbox [ngModel]="cancelJob()" (ngModelChange)="cancelJob.set($event)">
          {{ 'refund.cancelJob' | translate }}
        </mat-checkbox>
      }

      <mat-form-field>
        <mat-label>{{ 'common.reason' | translate }}</mat-label>
        <textarea
          matInput
          rows="3"
          maxlength="500"
          [ngModel]="reason()"
          (ngModelChange)="reason.set($event)"
        ></textarea>
        <mat-hint>{{ 'refund.reasonHint' | translate }}</mat-hint>
      </mat-form-field>
    </mat-dialog-content>

    <mat-dialog-actions class="neo-dialog__actions">
      <button mat-button type="button" [disabled]="busy()" (click)="close(false)">
        {{ 'common.cancel' | translate }}
      </button>
      <button mat-flat-button type="button" [disabled]="!canSubmit() || busy()" (click)="submit()">
        {{ 'refund.submit' | translate: { amount: (total() | money) } }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .radio {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      margin-block: var(--space-2);
    }
  `,
})
export class RefundDialogComponent {
  readonly data = inject<RefundDialogData>(MAT_DIALOG_DATA);
  private readonly api = inject(MoneyApiService);
  private readonly ref = inject<MatDialogRef<RefundDialogComponent, boolean>>(MatDialogRef);

  readonly partial = signal(false);
  readonly amount = signal<number | null>(null);
  readonly cancelJob = signal(false);
  readonly reason = signal('');
  readonly busy = signal(false);
  readonly problem = signal<Problem | null>(null);

  /**
   * Account: balance or card. Anonymous: card, or a voucher when the bank
   * cannot take it back (plan section 2.5). The API lists exactly what this
   * payment allows and refuses anything else.
   */
  readonly destinations = computed(() => [...this.data.options.externalDestinations]);

  readonly destination = signal<string | null>(null);

  readonly total = computed(() =>
    this.partial() ? Number(this.amount() ?? 0) : this.data.options.refundable,
  );

  readonly canSubmit = computed(() => {
    const amount = this.total();
    return this.reason().trim().length > 0 && amount > 0 && amount <= this.data.options.refundable;
  });

  constructor() {
    this.destination.set(this.destinations()[0] ?? null);
  }

  close(done: boolean): void {
    this.ref.close(done);
  }

  async submit(): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);

    try {
      await firstValueFrom(
        this.api.refund(this.data.paymentId, {
          amount: this.partial() ? Math.round(this.total() * 100) / 100 : null,
          destination: this.destinations().length > 1 ? this.destination() : null,
          reason: this.reason().trim(),
          cancelJob: this.cancelJob(),
        }),
      );
      this.close(true);
    } catch (error) {
      this.problem.set(toProblem(error));
      this.busy.set(false);
    }
  }
}
