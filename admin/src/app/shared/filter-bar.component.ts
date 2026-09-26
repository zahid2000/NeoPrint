import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * The filter row that sits between a page's header and its table.
 *
 * A wrapper rather than a widget: each page's filters differ, but where they
 * are, how they are spaced and how they behave at phone width should not
 * (docs/DESIGN_SYSTEM.md section 5.3).
 */
@Component({
  selector: 'app-filter-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="filters" role="search">
      <ng-content />
    </div>
  `,
  styles: `
    .filters {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-3);
      align-items: center;
      padding: var(--space-3) var(--space-4);
      border: 1px solid var(--neutral-200);
      border-radius: var(--radius-md);
      background: var(--neutral-0);
    }

    /*
     * Material form fields carry a reserved line for their hint text. In a
     * filter row there is no hint, and the gap it leaves makes the row look
     * misaligned against the buttons beside it.
     */
    .filters ::ng-deep .mat-mdc-form-field-subscript-wrapper {
      display: none;
    }

    .filters ::ng-deep .mat-mdc-form-field {
      min-width: 200px;
    }

    @media (max-width: 700px) {
      .filters ::ng-deep .mat-mdc-form-field {
        min-width: 100%;
      }
    }
  `,
})
export class FilterBarComponent {}
