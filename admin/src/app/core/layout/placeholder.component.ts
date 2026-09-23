import { Component, input } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { TranslatePipe } from '@ngx-translate/core';

/**
 * A page that the menu already reaches but whose screen is not built yet.
 *
 * The navigation is driven by permissions, so a role carrying settings.manage
 * would otherwise open a blank route. Saying plainly that the page is still to
 * come is better than a dead end.
 */
@Component({
  selector: 'app-placeholder',
  imports: [MatCardModule, MatIconModule, TranslatePipe],
  template: `
    <mat-card class="placeholder">
      <mat-card-content>
        <mat-icon class="placeholder__icon">{{ icon() }}</mat-icon>
        <h1>{{ titleKey() | translate }}</h1>
        <p>{{ bodyKey() | translate }}</p>
      </mat-card-content>
    </mat-card>
  `,
  styles: `
    .placeholder {
      max-width: 40rem;
      margin: 2rem auto;
      text-align: center;

      &__icon {
        font-size: 3rem;
        width: 3rem;
        height: 3rem;
        color: var(--mat-sys-primary);
      }

      h1 {
        margin: 0.5rem 0;
        font-size: 1.5rem;
      }

      p {
        margin: 0;
        color: var(--mat-sys-on-surface-variant);
      }
    }
  `,
})
export class PlaceholderComponent {
  readonly titleKey = input.required<string>();
  readonly bodyKey = input<string>('placeholder.body');
  readonly icon = input<string>('construction');
}
