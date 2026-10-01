import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

/**
 * The tones a status can have. Named after the meaning, not the colour, so a
 * template never has to know which colour a status is.
 */
export type StatusTone = 'success' | 'warning' | 'danger' | 'neutral' | 'info';

/**
 * Every status the panel shows, mapped to a tone and an icon.
 *
 * The keys are the values the API returns (kiosk, job, payment and device
 * statuses), so a caller passes the status straight through.
 */
const tones: Record<string, StatusTone> = {
  // Kiosks
  Online: 'success',
  Paused: 'warning',
  Maintenance: 'warning',
  Offline: 'danger',
  Disabled: 'danger',
  NotConfigured: 'neutral',
  NotActivated: 'neutral',

  // Devices
  Active: 'success',
  PendingActivation: 'warning',
  Revoked: 'danger',

  // Storage providers and sync jobs
  Healthy: 'success',
  Unhealthy: 'danger',
  Unknown: 'neutral',
  Running: 'info',
  Verifying: 'info',

  // Jobs and payments
  PendingPayment: 'warning',
  Paid: 'info',
  Expired: 'neutral',
  Completed: 'success',
  Succeeded: 'success',
  Pending: 'warning',
  Queued: 'warning',
  Suspected: 'warning',
  Failed: 'danger',
  Interrupted: 'danger',
  Cancelled: 'neutral',
  Draft: 'neutral',
  Idle: 'neutral',

  // Payments, refunds, fiscal receipts, vouchers, customers and claims (step 4)
  Refunded: 'neutral',
  PartiallyRefunded: 'warning',
  RefundFailed: 'danger',
  Issued: 'success',
  Redeemed: 'neutral',
  Blocked: 'danger',
  Open: 'info',
  Answered: 'success',
  Closed: 'neutral',
  Inactive: 'neutral',

  // Kiosk commands
  Sent: 'info',
  Acknowledged: 'info',
  Printing: 'info',
  Processing: 'info',
};

const icons: Record<StatusTone, string> = {
  success: 'check_circle',
  warning: 'warning',
  danger: 'error',
  neutral: 'radio_button_unchecked',
  info: 'progress_activity',
};

/**
 * A status, shown as colour plus icon plus text.
 *
 * All three, always: colour alone carries no meaning for somebody who cannot
 * tell these colours apart, or on a kiosk screen in daylight
 * (docs/DESIGN_SYSTEM.md principle 6).
 */
@Component({
  selector: 'app-status-chip',
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="chip" [class]="'chip--' + tone()">
      <mat-icon class="chip__icon" aria-hidden="true">{{ icon() }}</mat-icon>
      <span>{{ label() }}</span>
    </span>
  `,
  styles: `
    .chip {
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

    .chip__icon {
      width: 14px;
      height: 14px;
      font-size: 14px;
    }

    .chip--success {
      background: var(--success-bg);
      color: var(--success-500);
    }

    .chip--warning {
      background: var(--warning-bg);
      color: var(--warning-500);
    }

    .chip--danger {
      background: var(--danger-bg);
      color: var(--danger-500);
    }

    .chip--info {
      background: var(--info-bg);
      color: var(--info-500);
    }

    .chip--neutral {
      background: var(--neutral-100);
      color: var(--neutral-600);
    }
  `,
})
export class StatusChipComponent {
  /** The status as the API spells it. */
  readonly status = input.required<string>();

  /** The translated text. Defaults to the status itself. */
  readonly text = input<string | undefined>(undefined);

  /** Overrides the tone for a status this component does not know. */
  readonly toneOverride = input<StatusTone | undefined>(undefined);

  readonly tone = computed<StatusTone>(
    () => this.toneOverride() ?? tones[this.status()] ?? 'neutral',
  );

  readonly icon = computed(() => icons[this.tone()]);

  readonly label = computed(() => this.text() ?? this.status());
}
