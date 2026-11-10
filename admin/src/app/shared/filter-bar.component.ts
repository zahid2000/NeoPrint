import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { Router } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';

/** Where each page remembers whether its filters are open (T-424). */
const storageKey = (path: string): string => `neo.filters.${path}`;

/**
 * The filter row that sits between a page's header and its table.
 *
 * A wrapper rather than a widget: each page's filters differ, but where they
 * are, how they are spaced and how they behave at phone width should not
 * (docs/DESIGN_SYSTEM.md section 5.3).
 *
 * It opens and closes (T-424): closed, it is one line - "Filtrlər", how many
 * are set - and the table moves up. Each page remembers the choice in this
 * browser; a page with a filter set opens it.
 */
@Component({
  selector: 'app-filter-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatIconModule, TranslatePipe],
  template: `
    <div class="filters" role="search" [class.filters--closed]="!open()">
      <button
        mat-button
        type="button"
        class="filters__toggle"
        [attr.aria-expanded]="open()"
        (click)="toggle()"
      >
        <mat-icon>filter_list</mat-icon>
        {{ 'common.filters' | translate }}
        @if (active() > 0) {
          <span class="filters__count">{{ active() }}</span>
        }
        <mat-icon iconPositionEnd class="filters__chevron">expand_more</mat-icon>
      </button>
      @if (open()) {
        <div class="filters__fields">
          <ng-content />
        </div>
      }
    </div>
  `,
  styles: `
    .filters {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      padding: var(--space-2) var(--space-4) var(--space-3);
      border: 1px solid var(--neutral-200);
      border-radius: var(--radius-md);
      background: var(--neutral-0);
    }

    .filters--closed {
      padding-bottom: var(--space-2);
    }

    .filters__toggle {
      align-self: flex-start;
    }

    .filters__chevron {
      transition: transform var(--motion-fast) var(--motion-easing);
    }

    .filters:not(.filters--closed) .filters__chevron {
      transform: rotate(180deg);
    }

    .filters__count {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-width: var(--space-6);
      margin-left: var(--space-1);
      padding: 0 var(--space-1);
      border-radius: var(--radius-full);
      background: var(--brand-500);
      color: var(--neutral-0);
      font-size: var(--font-size-caption);
    }

    .filters__fields {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-3);
      /* Top, not centre: a field showing an error grows downwards without moving the others. */
      align-items: flex-start;
    }

    /*
     * Material form fields carry a reserved line for their hint text. In a
     * filter row there is no hint, and the gap it leaves makes the row look
     * misaligned against the buttons beside it. A field that cannot read what
     * was typed (a date) still says why underneath.
     */
    .filters__fields
      ::ng-deep
      .mat-mdc-form-field:not(.mat-form-field-invalid)
      .mat-mdc-form-field-subscript-wrapper {
      display: none;
    }

    .filters__fields ::ng-deep .mat-mdc-form-field {
      min-width: 200px;
    }

    @media (max-width: 700px) {
      .filters__fields ::ng-deep .mat-mdc-form-field {
        min-width: 100%;
      }
    }
  `,
})
export class FilterBarComponent {
  private readonly path = inject(Router).url.split('?')[0];

  /** How many filters are set now; a page that knows says so, and the bar opens for it. */
  readonly active = input(0);

  private readonly chosen = signal<boolean | null>(read(this.path));

  /** Open as last left on this page; with nothing remembered, open when a filter is set. */
  readonly open = computed(() => this.chosen() ?? this.active() > 0);

  toggle(): void {
    const next = !this.open();
    this.chosen.set(next);
    try {
      localStorage.setItem(storageKey(this.path), next ? '1' : '0');
    } catch {
      // Private mode or blocked storage: it is only not remembered.
    }
  }
}

function read(path: string): boolean | null {
  try {
    const value = localStorage.getItem(storageKey(path));
    return value === null ? null : value === '1';
  } catch {
    return null;
  }
}
