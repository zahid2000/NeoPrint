import { describe, expect, it } from 'vitest';

import { KioskListItem } from '../../core/api/admin-api.service';
import { attentionFor, shownStatus, staleHeartbeatMs } from './dashboard.component';

function kiosk(overrides: Partial<KioskListItem> = {}): KioskListItem {
  return {
    id: 'k1',
    code: 'KSK-0001',
    name: 'Nizami',
    status: 'Online',
    address: 'Bakı',
    paperA4Current: 400,
    paperA4Capacity: 500,
    safeModeSince: null,
    lastHeartbeatAt: new Date().toISOString(),
    ...overrides,
  } as KioskListItem;
}

describe('attentionFor', () => {
  it('leaves a healthy kiosk alone, and one that is not meant to serve', () => {
    expect(attentionFor(kiosk())).toBeNull();
    expect(attentionFor(kiosk({ status: 'Disabled', paperA4Current: 0 }))).toBeNull();
    expect(attentionFor(kiosk({ status: 'NotActivated' }))).toBeNull();
  });

  it('names the most urgent reason only', () => {
    expect(
      attentionFor(kiosk({ status: 'Offline', safeModeSince: '2026-09-28T10:00:00Z' }))?.reason,
    ).toBe('safeMode');
    expect(attentionFor(kiosk({ status: 'Offline', paperA4Current: 0 }))?.reason).toBe('offline');
    expect(attentionFor(kiosk({ status: 'NotConfigured' }))?.reason).toBe('notConfigured');
    expect(attentionFor(kiosk({ status: 'Paused', paperA4Current: 0 }))?.reason).toBe('noPaper');
    expect(attentionFor(kiosk({ status: 'Maintenance' }))?.reason).toBe('maintenance');
    expect(attentionFor(kiosk({ status: 'Paused' }))?.reason).toBe('paused');
  });

  it('warns about paper below a fifth of the tray, and not about a kiosk without a tray', () => {
    const low = attentionFor(kiosk({ paperA4Current: 99 }));

    expect(low?.reason).toBe('lowPaper');
    expect(low?.tone).toBe('warning');
    expect(low?.params).toEqual({ current: 99 });
    expect(attentionFor(kiosk({ paperA4Current: 100 }))).toBeNull();
    expect(attentionFor(kiosk({ paperA4Current: 0, paperA4Capacity: 0 }))).toBeNull();
  });

  // T-258: a new kiosk's nought is a count nobody made, not an empty tray.
  it('asks for the counter to be set on a kiosk nobody has counted, rather than calling it out of paper', () => {
    const unset = attentionFor(kiosk({ paperA4Current: 0, paperA4Counted: false }));

    expect(unset?.reason).toBe('paperUnset');
    expect(unset?.tone).toBe('warning');

    expect(attentionFor(kiosk({ paperA4Current: 0, paperA4Counted: true }))?.reason).toBe('noPaper');
    expect(attentionFor(kiosk({ paperA4Current: 0 }))?.reason, 'an answer without the field').toBe('noPaper');
    expect(
      attentionFor(kiosk({ status: 'Offline', paperA4Current: 0, paperA4Counted: false }))?.reason,
      'being out of touch comes first',
    ).toBe('offline');
    expect(attentionFor(kiosk({ paperA4Capacity: 0, paperA4Current: 0, paperA4Counted: false }))).toBeNull();
  });

  it('counts a kiosk whose heartbeat stopped as out of touch, whatever its stored status says', () => {
    const now = Date.parse('2026-09-29T12:00:00Z');
    const at = (ms: number) => new Date(now - ms).toISOString();

    expect(attentionFor(kiosk({ lastHeartbeatAt: at(staleHeartbeatMs + 1000) }), now)?.reason).toBe(
      'offline',
    );
    expect(attentionFor(kiosk({ lastHeartbeatAt: at(staleHeartbeatMs - 1000) }), now)).toBeNull();
    expect(
      attentionFor(kiosk({ lastHeartbeatAt: null, status: 'NotConfigured' }), now)?.reason,
    ).toBe('notConfigured');
  });
});

describe('shownStatus', () => {
  // The count above the attention list must not call "Online" a kiosk the list calls out of touch (T-091).
  it('counts a silent kiosk as offline and leaves the rest as stored', () => {
    const now = Date.parse('2026-09-29T12:00:00Z');
    const silent = new Date(now - staleHeartbeatMs - 1000).toISOString();

    expect(shownStatus(kiosk({ lastHeartbeatAt: silent }), now)).toBe('Offline');
    expect(shownStatus(kiosk({ status: 'Paused', lastHeartbeatAt: silent }), now)).toBe('Offline');
    expect(shownStatus(kiosk(), now)).toBe('Online');
    expect(shownStatus(kiosk({ lastHeartbeatAt: null, status: 'NotConfigured' }), now)).toBe(
      'NotConfigured',
    );
    expect(shownStatus(kiosk({ status: 'Disabled', lastHeartbeatAt: silent }), now)).toBe(
      'Disabled',
    );
  });
});
