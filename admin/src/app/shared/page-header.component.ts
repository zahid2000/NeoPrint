import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * The top of every page: what this page is, one line about it, and the page's
 * own actions on the right (docs/DESIGN_SYSTEM.md section 5.3).
 *
 * Having it here rather than in each template is what keeps the heading size,
 * the spacing and the position of the primary action the same everywhere.
 */
@Component({
  selector: 'app-page-header',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="header">
      <div class="header__text">
        <h1>{{ title() }}</h1>

        @if (description()) {
          <p class="header__description">{{ description() }}</p>
        }
      </div>

      <div class="header__actions">
        <ng-content />
      </div>
    </header>
  `,
  styles: `
    .header {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-4);
      align-items: flex-start;
      justify-content: space-between;
    }

    .header__text {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
    }

    .header__description {
      max-width: 72ch;
      color: var(--neutral-600);
      font-size: var(--font-size-body-small);
    }

    .header__actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      align-items: center;
    }
  `,
})
export class PageHeaderComponent {
  readonly title = input.required<string>();

  /** One line: what is this page for, or what the list contains. */
  readonly description = input<string | undefined>(undefined);
}
