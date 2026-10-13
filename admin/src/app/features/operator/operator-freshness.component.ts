import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { TranslatePipe } from '@ngx-translate/core';

import { TimePipe } from '../../shared/format.pipes';

/**
 * How fresh what an operator screen shows is (docs/DESIGN_SYSTEM.md 5.4,
 * "Oflayn"): a quiet "last updated" line while the server answers, and a
 * notice saying how old the data is - and that nothing can be done with it -
 * when it does not.
 */
@Component({
  selector: 'app-operator-freshness',
  imports: [MatIconModule, TranslatePipe, TimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (offline()) {
      <div class="neo-notice" role="status">
        <mat-icon aria-hidden="true">link_off</mat-icon>
        <span>{{ 'operator.offline' | translate: { time: (shownAt() | time) } }}</span>
      </div>
    } @else if (at()) {
      <p class="updated">{{ 'operator.updated' | translate: { time: (shownAt() | time) } }}</p>
    }
  `,
  styles: `
    .updated {
      margin: 0;
      color: var(--neutral-600);
      font-size: var(--font-size-caption);
      text-align: center;
    }
  `,
})
export class OperatorFreshnessComponent {
  /** When the server gave what is on screen, in epoch milliseconds. */
  readonly at = input<number | null>(null);

  /** The last attempt to ask again failed. */
  readonly offline = input(false);

  protected shownAt(): Date | null {
    const at = this.at();

    return at === null ? null : new Date(at);
  }
}
