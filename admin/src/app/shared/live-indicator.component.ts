import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TranslatePipe } from '@ngx-translate/core';

import { AdminHubService } from '../core/realtime/admin-hub.service';
import { TimePipe } from './format.pipes';

/**
 * Whether what is on screen is still live.
 *
 * The design system asks for this next to any field that updates on its own
 * (section 5.3): without it, a panel whose connection dropped looks exactly
 * like a quiet system, and somebody acts on numbers that stopped moving ten
 * minutes ago.
 */
@Component({
  selector: 'app-live-indicator',
  imports: [MatIconModule, MatTooltipModule, TranslatePipe, TimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="live" [class]="'live--' + state()" [matTooltip]="tooltip() | translate">
      <mat-icon class="live__icon" aria-hidden="true">{{ icon() }}</mat-icon>
      <span>{{ label() | translate }}</span>

      @if (lastEventAt(); as at) {
        <span class="live__time">{{ at | time }}</span>
      }
    </span>
  `,
  styles: `
    .live {
      display: inline-flex;
      gap: var(--space-1);
      align-items: center;
      height: var(--status-chip-height);
      padding: 0 var(--space-2);
      border-radius: var(--radius-full);
      font-size: var(--font-size-caption);
      font-weight: var(--font-weight-medium);
      white-space: nowrap;
    }

    .live__icon {
      width: 14px;
      height: 14px;
      font-size: 14px;
    }

    .live__time {
      font-variant-numeric: tabular-nums;
      opacity: 0.8;
    }

    .live--connected {
      background: var(--success-bg);
      color: var(--success-500);
    }

    .live--reconnecting {
      background: var(--warning-bg);
      color: var(--warning-500);
    }

    .live--disconnected {
      background: var(--neutral-100);
      color: var(--neutral-600);
    }
  `,
})
export class LiveIndicatorComponent {
  private readonly hub = inject(AdminHubService);

  readonly state = this.hub.connectionState;
  readonly lastEventAt = this.hub.lastEventAt;

  readonly icon = computed(
    () =>
      ({
        connected: 'sync',
        reconnecting: 'progress_activity',
        disconnected: 'link_off',
      })[this.state()],
  );

  readonly label = computed(() => `live.${this.state()}`);

  readonly tooltip = computed(() => `live.${this.state()}Hint`);
}
