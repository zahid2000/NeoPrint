import { KioskUpdateStatus } from '../../core/api/releases-api.service';
import { StatusTone } from '../../shared/status-chip.component';

/** A status chip's tone and icon (docs/DESIGN_SYSTEM.md: colour + icon + text). */
export interface Chip {
  readonly tone: StatusTone;
  readonly icon: string;
}

/**
 * Where an update stands on a kiosk (docs/STATE_MACHINES.md "Update"): the
 * same chips on the releases page and on the kiosk's update tab (T-326).
 */
export const updateChips: Record<KioskUpdateStatus, Chip> = {
  Downloading: { tone: 'info', icon: 'download' },
  Ready: { tone: 'info', icon: 'schedule' },
  WaitingForIdle: { tone: 'info', icon: 'hourglass_empty' },
  Draining: { tone: 'warning', icon: 'do_not_disturb_on' },
  Applying: { tone: 'info', icon: 'progress_activity' },
  HealthCheck: { tone: 'info', icon: 'progress_activity' },
  Succeeded: { tone: 'success', icon: 'check_circle' },
  Failed: { tone: 'danger', icon: 'error' },
  RolledBack: { tone: 'warning', icon: 'undo' },
};
