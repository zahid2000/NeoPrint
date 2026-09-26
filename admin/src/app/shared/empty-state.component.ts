import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

/**
 * What a list shows when it has nothing to show.
 *
 * An icon, one sentence that says why it is empty, and - where there is one -
 * the action that would fill it. "No data" on its own is not an empty state
 * (docs/DESIGN_SYSTEM.md section 4).
 */
@Component({
  selector: 'app-empty-state',
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="empty">
      <mat-icon class="empty__icon" aria-hidden="true">{{ icon() }}</mat-icon>
      <p class="empty__message">{{ message() }}</p>

      @if (hint()) {
        <p class="empty__hint">{{ hint() }}</p>
      }

      <div class="empty__action">
        <ng-content />
      </div>
    </div>
  `,
  styles: `
    .empty {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      align-items: center;
      padding: var(--space-12) var(--space-6);
      text-align: center;
    }

    .empty__icon {
      width: 40px;
      height: 40px;
      color: var(--neutral-400);
      font-size: 40px;
    }

    .empty__message {
      color: var(--neutral-800);
      font-weight: var(--font-weight-medium);
    }

    .empty__hint {
      max-width: 44ch;
      color: var(--neutral-600);
      font-size: var(--font-size-body-small);
    }

    .empty__action:empty {
      display: none;
    }

    .empty__action {
      margin-top: var(--space-2);
    }
  `,
})
export class EmptyStateComponent {
  /** A Material Symbols name. */
  readonly icon = input('inbox');

  /** One sentence: why is this empty? */
  readonly message = input.required<string>();

  /** Optional second line: what would put something here. */
  readonly hint = input<string | undefined>(undefined);
}
