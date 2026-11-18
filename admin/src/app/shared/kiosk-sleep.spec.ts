import { describe, expect, it } from 'vitest';

import { KioskListItem } from '../core/api/admin-api.service';
import { shownStatus } from '../features/dashboard/dashboard.component';
import { isAsleep } from './kiosk-sleep';

/** T-448: a kiosk asleep for the night is quiet on purpose, until it should have woken. */
describe('isAsleep', () => {
  const morning = Date.UTC(2026, 10, 19, 6, 0, 0);
  const kiosk = {
    status: 'Online',
    lastHeartbeatAt: new Date(Date.UTC(2026, 10, 18, 23, 40, 0)).toISOString(),
    sleepingUntil: new Date(morning).toISOString(),
  } as KioskListItem;

  it('is asleep until the wake time and its grace, then silent again', () => {
    const night = Date.UTC(2026, 10, 19, 3, 0, 0);
    expect(isAsleep(kiosk, night)).toBe(true);
    expect(shownStatus(kiosk, night)).toBe('Online');

    const late = morning + 11 * 60 * 1000;
    expect(isAsleep(kiosk, late)).toBe(false);
    expect(shownStatus(kiosk, late)).toBe('Offline');
  });

  it('is awake when the heartbeat said nothing about sleep', () => {
    expect(isAsleep({ ...kiosk, sleepingUntil: null }, Date.UTC(2026, 10, 19, 3, 0, 0))).toBe(false);
  });
});
