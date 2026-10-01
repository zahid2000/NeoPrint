import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { TranslatePipe } from '@ngx-translate/core';

/** The first moment of the chosen day in the viewer's time zone, as the API expects it. */
export const startOfDay = (date: Date | null): string | null =>
  date ? new Date(date.getFullYear(), date.getMonth(), date.getDate()).toISOString() : null;

/** The last moment of the chosen day, so "to 29.09" includes the whole of the 29th. */
export const endOfDay = (date: Date | null): string | null =>
  date
    ? new Date(date.getFullYear(), date.getMonth(), date.getDate(), 23, 59, 59, 999).toISOString()
    : null;

/** `yyyy-MM-dd` of the day as the viewer picked it: the report's days are Baku days. */
export const dayString = (date: Date): string =>
  `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;

/**
 * The pager under every money list: the count, and previous / next when
 * there is more than one page (docs/DESIGN_SYSTEM.md section 5.3).
 */
@Component({
  selector: 'app-money-pager',
  imports: [MatButtonModule, MatIconModule, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="pager">
      <span class="neo-caption">{{ countKey() | translate: { total: total() } }}</span>
      @if (pages() > 1) {
        <button mat-button type="button" [disabled]="page() === 1" (click)="go.emit(page() - 1)">
          <mat-icon>chevron_left</mat-icon>
          {{ 'money.previous' | translate }}
        </button>
        <span class="neo-caption">{{
          'money.pageOf' | translate: { page: page(), pages: pages() }
        }}</span>
        <button
          mat-button
          type="button"
          [disabled]="page() === pages()"
          (click)="go.emit(page() + 1)"
        >
          {{ 'money.next' | translate }}
          <mat-icon iconPositionEnd>chevron_right</mat-icon>
        </button>
      }
    </div>
  `,
  styles: `
    .pager {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-3);
      align-items: center;
      justify-content: flex-end;
      margin-block: var(--space-4);
    }
  `,
})
export class PagerComponent {
  readonly total = input.required<number>();
  readonly page = input.required<number>();
  readonly pageSize = input.required<number>();
  readonly countKey = input.required<string>();
  readonly go = output<number>();

  readonly pages = computed(() => Math.max(1, Math.ceil(this.total() / this.pageSize())));
}
