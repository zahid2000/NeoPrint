import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService } from '../../core/api/admin-api.service';
import { KioskSupplies, PaperRefillMode, PaperStock, lowPaperShare } from '../../core/api/step3.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { Problem, toProblem } from '../../shared/problem';

/** The trays the panel shows, in order. */
const papers = ['A4', 'A3'] as const;

/**
 * Kiosk detail → Supplies (plan section 2.15): the paper counter per tray,
 * the refill an operator records after loading paper, and the movements
 * that changed the counter.
 */
@Component({
  selector: 'app-kiosk-supplies',
  imports: [
    FormsModule,
    RouterLink,
    MatButtonModule,
    MatButtonToggleModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatTableModule,
    TranslatePipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    DateTimePipe,
  ],
  template: `
    <p class="neo-muted neo-body-small">{{ 'supplies.tabDescription' | translate }}</p>

    @if (problem(); as problem) {
      <app-error-banner [problem]="problem" effect="supplies.loadFailedEffect" (retry)="load()" />
    }

    @if (loading()) {
      <div class="neo-skeleton neo-skeleton--block"></div>
    } @else if (supplies(); as current) {
      <div class="supplies__trays">
        @for (tray of trays(); track tray.paper) {
          <div class="neo-card supplies__tray">
            <strong>{{ 'supplies.paper' + tray.paper | translate }}</strong>
            @if (tray.stock.capacity === 0) {
              <p class="neo-muted neo-body-small">{{ 'supplies.noTray' | translate }}</p>
            } @else {
              <p class="supplies__level">
                {{ 'supplies.level' | translate: { current: tray.stock.current, capacity: tray.stock.capacity } }}
              </p>
              <div
                class="supplies__bar"
                role="progressbar"
                [attr.aria-valuenow]="tray.stock.current"
                aria-valuemin="0"
                [attr.aria-valuemax]="tray.stock.capacity"
              >
                <span
                  class="supplies__fill"
                  [class.supplies__fill--low]="tray.low"
                  [style.width.%]="(tray.stock.current / tray.stock.capacity) * 100"
                ></span>
              </div>
              @if (tray.stock.current === 0) {
                <p class="neo-field-error" role="status">
                  <mat-icon aria-hidden="true">error</mat-icon>
                  {{ 'supplies.empty' | translate }}
                </p>
              }
              <p class="neo-caption">
                @if (tray.stock.lastRefillAt) {
                  {{ 'supplies.lastRefill' | translate: { at: (tray.stock.lastRefillAt | dateTime) } }}
                } @else {
                  {{ 'supplies.neverRefilled' | translate }}
                }
              </p>
            }
          </div>
        }
      </div>

      @if (canRefill() && refillable().length > 0) {
        <section class="neo-card supplies__refill">
          <h3>{{ 'supplies.refillTitle' | translate }}</h3>
          <div class="supplies__form">
            @if (refillable().length > 1) {
              <mat-button-toggle-group
                [attr.aria-label]="'supplies.refillPaper' | translate"
                [ngModel]="paper()"
                (ngModelChange)="paper.set($event)"
              >
                @for (option of refillable(); track option) {
                  <mat-button-toggle [value]="option">{{ option }}</mat-button-toggle>
                }
              </mat-button-toggle-group>
            }
            <mat-button-toggle-group
              [attr.aria-label]="'supplies.refillMode' | translate"
              [ngModel]="mode()"
              (ngModelChange)="mode.set($event)"
            >
              @for (option of modes; track option) {
                <mat-button-toggle [value]="option">{{ 'supplies.mode.' + option | translate }}</mat-button-toggle>
              }
            </mat-button-toggle-group>
            @if (mode() !== 'Full') {
              <mat-form-field>
                <mat-label>{{ 'supplies.sheets' | translate }}</mat-label>
                <input
                  matInput
                  type="number"
                  min="0"
                  [max]="maxSheets()"
                  [ngModel]="sheets()"
                  (ngModelChange)="sheets.set($event)"
                />
              </mat-form-field>
            }
          </div>
          <p class="neo-caption">{{ 'supplies.modeHint.' + mode() | translate }}</p>
          @if (!sheetsValid() && sheets() !== null) {
            <p class="neo-field-error" role="alert">
              <mat-icon aria-hidden="true">error</mat-icon>
              {{ 'supplies.invalidSheets' | translate }}
            </p>
          }
          <div class="supplies__actions">
            <button mat-flat-button type="button" [disabled]="!sheetsValid() || saving()" (click)="refill()">
              @if (saving()) {
                <mat-spinner diameter="18" class="neo-button-spinner" />
              }
              {{ 'supplies.refill' | translate }}
            </button>
          </div>
        </section>
      }

      <h3>{{ 'supplies.eventsTitle' | translate }}</h3>
      @if (current.events.length === 0) {
        <app-empty-state icon="inventory_2" [message]="'supplies.eventsEmpty' | translate" />
      } @else {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="current.events">
            <ng-container matColumnDef="at">
              <th mat-header-cell *matHeaderCellDef>{{ 'supplies.colAt' | translate }}</th>
              <td mat-cell *matCellDef="let event">{{ event.at | dateTime }}</td>
            </ng-container>
            <ng-container matColumnDef="event">
              <th mat-header-cell *matHeaderCellDef>{{ 'supplies.colEvent' | translate }}</th>
              <td mat-cell *matCellDef="let event">
                @if (event.jobId) {
                  <a [routerLink]="['/print-jobs', event.jobId]">{{ 'supplies.event.' + event.type | translate }}</a>
                } @else {
                  {{ 'supplies.event.' + event.type | translate }}
                }
              </td>
            </ng-container>
            <ng-container matColumnDef="paper">
              <th mat-header-cell *matHeaderCellDef>{{ 'supplies.colPaper' | translate }}</th>
              <td mat-cell *matCellDef="let event">{{ event.resource }}</td>
            </ng-container>
            <ng-container matColumnDef="change">
              <th mat-header-cell *matHeaderCellDef>{{ 'supplies.colChange' | translate }}</th>
              <td mat-cell *matCellDef="let event">
                <span class="supplies__delta" [class.supplies__delta--added]="event.delta > 0">
                  {{ (event.delta > 0 ? '+' : '') + event.delta }}
                </span>
              </td>
            </ng-container>
            <ng-container matColumnDef="after">
              <th mat-header-cell *matHeaderCellDef>{{ 'supplies.colAfter' | translate }}</th>
              <td mat-cell *matCellDef="let event">{{ event.after }}</td>
            </ng-container>

            <tr mat-header-row *matHeaderRowDef="columns"></tr>
            <tr mat-row *matRowDef="let row; columns: columns"></tr>
          </table>
        </div>
      }
    }
  `,
  styles: `
    .supplies__trays {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
      gap: var(--space-4);
      margin-block: var(--space-4);
    }

    .supplies__tray,
    .supplies__refill {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      padding: var(--space-4);
    }

    .supplies__refill {
      margin-block-end: var(--space-6);
    }

    .supplies__level {
      margin: 0;
      font-size: var(--font-size-h2);
      font-weight: var(--font-weight-semibold);
    }

    .supplies__bar {
      height: var(--space-2);
      border-radius: var(--radius-sm);
      background: var(--neutral-100);
      overflow: hidden;
    }

    .supplies__fill {
      display: block;
      height: 100%;
      background: var(--success-500);
    }

    .supplies__fill--low {
      background: var(--warning-500);
    }

    .supplies__form {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-4);
      align-items: center;
    }

    .supplies__actions {
      display: flex;
      justify-content: flex-end;
    }

    .supplies__delta {
      font-variant-numeric: tabular-nums;
    }

    .supplies__delta--added {
      color: var(--success-500);
      font-weight: var(--font-weight-semibold);
    }
  `,
})
export class KioskSuppliesComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly auth = inject(AuthService);
  private readonly notify = inject(NotifyService);

  readonly kioskId = input.required<string>();

  readonly columns = ['at', 'event', 'paper', 'change', 'after'];
  readonly modes: readonly PaperRefillMode[] = ['Add', 'Full', 'Exact'];

  readonly supplies = signal<KioskSupplies | null>(null);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);
  readonly saving = signal(false);

  readonly paper = signal<string>('A4');
  readonly mode = signal<PaperRefillMode>('Add');
  readonly sheets = signal<number | null>(null);

  readonly canRefill = computed(() => this.auth.can(Permissions.paperRefill));

  readonly trays = computed(() => {
    const paper = this.supplies()?.paper ?? {};

    return papers
      .filter((name) => paper[name] !== undefined)
      .map((name) => {
        const stock: PaperStock = paper[name];

        return {
          paper: name,
          stock,
          low: stock.capacity > 0 && stock.current < stock.capacity * lowPaperShare,
        };
      });
  });

  /** Only a tray with a capacity can be filled. */
  readonly refillable = computed(() =>
    this.trays()
      .filter((tray) => tray.stock.capacity > 0)
      .map((tray) => tray.paper as string),
  );

  /** Adding may only fill the tray up to its capacity; a recount may name any count up to it. */
  readonly maxSheets = computed(() => {
    const stock = this.supplies()?.paper[this.paper()];

    return !stock ? 0 : this.mode() === 'Add' ? stock.capacity - stock.current : stock.capacity;
  });

  readonly sheetsValid = computed(() => {
    if (this.mode() === 'Full') {
      return true;
    }

    const sheets = this.sheets();
    const minimum = this.mode() === 'Add' ? 1 : 0;

    return sheets !== null && Number.isInteger(sheets) && sheets >= minimum && sheets <= this.maxSheets();
  });

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.problem.set(null);

    try {
      this.apply(await firstValueFrom(this.api.kioskSupplies(this.kioskId())));
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }

  async refill(): Promise<void> {
    if (!this.sheetsValid()) {
      return;
    }

    this.saving.set(true);

    try {
      const sheets = this.mode() === 'Full' ? 0 : (this.sheets() ?? 0);
      this.apply(await firstValueFrom(this.api.refillPaper(this.kioskId(), this.paper(), this.mode(), sheets)));
      this.sheets.set(null);
      this.notify.success('supplies.refilled');
    } catch (error) {
      this.notify.error(toProblem(error).message);
    } finally {
      this.saving.set(false);
    }
  }

  private apply(supplies: KioskSupplies): void {
    this.supplies.set(supplies);

    const refillable = this.refillable();

    if (!refillable.includes(this.paper()) && refillable.length > 0) {
      this.paper.set(refillable[0]);
    }
  }
}
