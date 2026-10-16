import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { KioskListItem } from '../../core/api/admin-api.service';
import { MonitoringApiService } from '../../core/api/monitoring-api.service';
import { SupplyChannel, SupplyRule, SupplyRuleInput } from '../../core/api/step7.models';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { Problem, toProblem } from '../../shared/problem';

export interface SupplyRuleDialogData {
  /** The rule being changed, or null for a new one. */
  readonly rule: SupplyRule | null;

  /** The kiosks a new rule is for: this kiosk's tab makes rules for this kiosk only. */
  readonly kioskIds: readonly string[];

  /** Shown in the title: "KSK-9002 üçün qayda". Empty on the rules page. */
  readonly kioskCode: string;

  /**
   * The rules page (T-213): the rule may be for every kiosk or for some, chosen
   * here. Absent on a kiosk's own tab, where the kiosk is given.
   */
  readonly kiosks?: readonly KioskListItem[];
}

/** What a rule may watch (plan section 2.15). */
export const supplyResources = ['PaperA4', 'PaperA3', 'Toner', 'Drum', 'WasteToner', 'Other'] as const;

/**
 * The channels a rule may tell. Telegram is not offered: the channel opens
 * after the pilot (T-243) and an option that does nothing is not shown.
 */
export const supplyChannels: readonly SupplyChannel[] = ['AdminPanel', 'OperatorPanel', 'Email'];

export const taskPriorities = ['Low', 'Normal', 'High', 'Critical'] as const;

/**
 * A supply alert rule for one kiosk (plan section 2.15, docs/DESIGN_SYSTEM.md
 * "Sərf tab-ına əlavələr"): what is watched, the two limits, who is told, and
 * whether a service task opens with the alert.
 */
@Component({
  selector: 'app-supply-rule-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatCheckboxModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatSlideToggleModule,
    TranslatePipe,
    ErrorBannerComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>
      @if (data.kiosks) {
        {{ (data.rule ? 'ops.rules.editTitle' : 'ops.rules.newTitle') | translate }}
      } @else {
        {{ (data.rule ? 'monitoring.rules.editTitle' : 'monitoring.rules.newTitle') | translate: { kiosk: data.kioskCode } }}
      }
    </h2>
    <mat-dialog-content class="neo-dialog">
      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="monitoring.rules.saveFailedEffect" />
      }

      <div class="rule__form">
        @if (data.kiosks) {
          <mat-form-field class="rule__wide">
            <mat-label>{{ 'ops.rules.name' | translate }}</mat-label>
            <input matInput maxlength="80" [ngModel]="name()" (ngModelChange)="name.set($event)" />
          </mat-form-field>
          <mat-form-field class="rule__wide">
            <mat-label>{{ 'ops.rules.where' | translate }}</mat-label>
            <mat-select multiple [ngModel]="kioskIds()" (ngModelChange)="kioskIds.set($event)">
              @for (kiosk of data.kiosks; track kiosk.id) {
                <mat-option [value]="kiosk.id">{{ kiosk.code }} — {{ kiosk.name }}</mat-option>
              }
            </mat-select>
            <mat-hint>{{ 'ops.rules.whereHint' | translate }}</mat-hint>
          </mat-form-field>
        }
        <mat-form-field>
          <mat-label>{{ 'monitoring.rules.resource' | translate }}</mat-label>
          <mat-select [ngModel]="resource()" (ngModelChange)="setResource($event)">
            @for (option of resources; track option) {
              <mat-option [value]="option">{{ 'monitoring.resource.' + option | translate }}</mat-option>
            }
          </mat-select>
        </mat-form-field>

        @if (!paper()) {
          <mat-form-field>
            <mat-label>{{ 'monitoring.rules.variant' | translate }}</mat-label>
            <input matInput maxlength="64" [ngModel]="variant()" (ngModelChange)="variant.set($event)" />
            <mat-hint>{{ 'monitoring.rules.variantHint' | translate }}</mat-hint>
          </mat-form-field>
        }

        @if (paper()) {
          <mat-form-field>
            <mat-label>{{ 'monitoring.rules.unit' | translate }}</mat-label>
            <mat-select [ngModel]="unit()" (ngModelChange)="unit.set($event)">
              <mat-option value="Sheets">{{ 'monitoring.unitName.Sheets' | translate }}</mat-option>
              <mat-option value="Percent">{{ 'monitoring.unitName.Percent' | translate }}</mat-option>
            </mat-select>
          </mat-form-field>
        }

        <mat-form-field>
          <mat-label>{{ 'monitoring.rules.warning' | translate: { unit: unitText() } }}</mat-label>
          <input matInput type="number" min="1" [ngModel]="warning()" (ngModelChange)="warning.set($event)" />
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'monitoring.rules.critical' | translate: { unit: unitText() } }}</mat-label>
          <input matInput type="number" min="1" [ngModel]="critical()" (ngModelChange)="critical.set($event)" />
          <mat-hint>{{ 'monitoring.rules.thresholdHint' | translate }}</mat-hint>
        </mat-form-field>
      </div>

      @if (thresholdProblem(); as key) {
        <p class="neo-field-error" role="alert">{{ key | translate }}</p>
      }

      <fieldset class="rule__channels">
        <legend>{{ 'monitoring.rules.channels' | translate }}</legend>
        @for (channel of channels; track channel) {
          <mat-checkbox [checked]="selected().includes(channel)" (change)="toggle(channel, $event.checked)">
            {{ 'monitoring.channel.' + channel | translate }}
          </mat-checkbox>
        }
      </fieldset>

      <div class="rule__task">
        <mat-slide-toggle [checked]="createTask()" (change)="createTask.set($event.checked)">
          {{ 'monitoring.rules.createTask' | translate }}
        </mat-slide-toggle>
        @if (createTask()) {
          <mat-form-field>
            <mat-label>{{ 'monitoring.rules.priority' | translate }}</mat-label>
            <mat-select [ngModel]="priority()" (ngModelChange)="priority.set($event)">
              @for (option of priorities; track option) {
                <mat-option [value]="option">{{ 'operator.priority.' + option | translate }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        }
        <mat-slide-toggle [checked]="active()" (change)="active.set($event.checked)">
          {{ 'monitoring.rules.active' | translate }}
        </mat-slide-toggle>
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
  styles: `
    .rule__form {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
      gap: var(--space-2) var(--space-4);
    }

    .rule__wide {
      grid-column: 1 / -1;
    }

    .rule__channels {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2) var(--space-4);
      margin: var(--space-4) 0;
      padding: 0;
      border: none;
    }

    .rule__channels legend {
      margin-block-end: var(--space-2);
      font-weight: var(--font-weight-semibold);
    }

    .rule__task {
      display: flex;
      flex-direction: column;
      gap: var(--space-3);
      align-items: flex-start;
    }
  `,
})
export class SupplyRuleDialogComponent {
  readonly data = inject<SupplyRuleDialogData>(MAT_DIALOG_DATA);
  readonly ref = inject<MatDialogRef<SupplyRuleDialogComponent, boolean>>(MatDialogRef);
  private readonly api = inject(MonitoringApiService);

  readonly resources = supplyResources;
  readonly channels = supplyChannels;
  readonly priorities = taskPriorities;

  private readonly rule = this.data.rule;

  readonly resource = signal<string>(this.rule?.resource ?? 'Toner');
  readonly name = signal(this.rule?.name ?? '');
  readonly kioskIds = signal<string[]>([...(this.rule?.kioskIds ?? this.data.kioskIds)]);
  readonly variant = signal(this.rule?.resourceVariant ?? '');
  readonly unit = signal<'Sheets' | 'Percent'>(this.rule?.unit ?? 'Percent');
  readonly warning = signal<number | null>(this.rule?.warningThreshold ?? null);
  readonly critical = signal<number | null>(this.rule?.criticalThreshold ?? null);
  readonly selected = signal<SupplyChannel[]>([...(this.rule?.channels ?? ['AdminPanel', 'OperatorPanel'])]);
  readonly createTask = signal(this.rule?.createServiceTask ?? true);
  readonly priority = signal<string>(this.rule?.taskPriority ?? 'Normal');
  readonly active = signal(this.rule?.isActive ?? true);

  readonly busy = signal(false);
  readonly problem = signal<Problem | null>(null);

  readonly paper = computed(() => this.resource().startsWith('Paper'));
  readonly unitText = computed(() => (this.unit() === 'Percent' ? '%' : ''));

  /** Why the limits cannot be saved, said before the server is asked. */
  readonly thresholdProblem = computed<string | null>(() => {
    const warning = Number(this.warning());
    const critical = Number(this.critical());

    if (this.warning() === null || this.critical() === null) {
      return null;
    }

    if (!(warning > 0) || !(critical > 0)) {
      return 'monitoring.rules.positive';
    }

    if (this.unit() === 'Percent' && warning > 100) {
      return 'monitoring.rules.percentTooHigh';
    }

    return critical >= warning ? 'monitoring.rules.order' : null;
  });

  readonly valid = computed(
    () =>
      this.warning() !== null &&
      this.critical() !== null &&
      !this.thresholdProblem() &&
      this.selected().length > 0 &&
      (!this.data.kiosks || this.name().trim().length > 0),
  );

  setResource(resource: string): void {
    this.resource.set(resource);

    // Only paper is counted in sheets; a cartridge reports a percentage.
    if (!resource.startsWith('Paper')) {
      this.unit.set('Percent');
    }
  }

  toggle(channel: SupplyChannel, on: boolean): void {
    this.selected.update((list) => (on ? [...list, channel] : list.filter((item) => item !== channel)));
  }

  async save(): Promise<void> {
    const input: SupplyRuleInput = {
      name: this.data.kiosks ? this.name().trim() : (this.rule?.name ?? `${this.data.kioskCode} — ${this.resource()}`),
      kioskIds: this.data.kiosks ? this.kioskIds() : (this.rule?.kioskIds ?? this.data.kioskIds),
      resource: this.resource(),
      resourceVariant: this.paper() ? null : this.variant().trim() || null,
      unit: this.unit(),
      warningThreshold: Number(this.warning()),
      criticalThreshold: Number(this.critical()),
      channels: this.selected(),
      createServiceTask: this.createTask(),
      taskPriority: this.priority(),
      isActive: this.active(),
    };

    this.busy.set(true);
    this.problem.set(null);

    try {
      await firstValueFrom(this.rule ? this.api.updateSupplyRule(this.rule.id, input) : this.api.createSupplyRule(input));
      this.ref.close(true);
    } catch (error) {
      this.problem.set(toProblem(error));
      this.busy.set(false);
    }
  }
}
