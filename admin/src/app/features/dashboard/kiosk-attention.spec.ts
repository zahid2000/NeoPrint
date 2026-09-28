import { describe, expect, it } from 'vitest';

import { KioskListItem } from '../../core/api/admin-api.service';
import { attentionFor } from './dashboard.component';

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
    expect(attentionFor(kiosk({ status: 'Offline', safeModeSince: '2026-09-28T10:00:00Z' }))?.reason).toBe('safeMode');
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
});
