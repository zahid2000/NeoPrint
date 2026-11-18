import { KioskListItem } from '../core/api/admin-api.service';

/** How long a kiosk has, after the time it said it wakes, to call in - the backend's grace (T-448). */
export const wakeGraceMs = 10 * 60 * 1000;

/** Asleep for the night as its last heartbeat said, and not yet late to wake (T-448). */
export const isAsleep = (kiosk: KioskListItem, now: number): boolean =>
  !!kiosk.sleepingUntil && now < new Date(kiosk.sleepingUntil).getTime() + wakeGraceMs;
