import { describe, expect, it } from 'vitest';

import { formatBytes, formatCount, formatDateTime, formatMoney, formatTime } from './format.pipes';

/** docs/DESIGN_SYSTEM.md section 6, as it is written there. */
describe('format pipes', () => {
  it('writes money with a decimal comma and spaced thousands in az and ru', () => {
    expect(formatMoney(12.5)).toBe('12,50\u00a0₼');
    expect(formatMoney(1240)).toBe('1\u00a0240,00\u00a0₼');
    expect(formatMoney(1240, 'ru')).toBe('1\u00a0240,00\u00a0₼');
    expect(formatMoney(0.1)).toBe('0,10\u00a0₼');
    expect(formatMoney(-3)).toBe('−3,00\u00a0₼');
    expect(formatMoney(null)).toBe('—');
  });

  it('writes money the English way in en', () => {
    expect(formatMoney(1240, 'en')).toBe('1,240.00\u00a0₼');
    expect(formatMoney(12.5, 'en')).toBe('12.50\u00a0₼');
  });

  it('writes dates as dd.MM.yyyy HH:mm in every language', () => {
    // The format the panel showed before this rule: 9/27/26, 6:33 PM.
    expect(formatDateTime(new Date(2026, 8, 27, 18, 33))).toBe('27.09.2026 18:33');
    expect(formatDateTime(new Date(2026, 8, 7, 4, 5))).toBe('07.09.2026 04:05');
    expect(formatDateTime(undefined)).toBe('—');
    expect(formatDateTime('not a date')).toBe('—');
  });

  it('says today and yesterday in words', () => {
    const now = new Date(2026, 8, 27, 20, 0);
    const words = { today: 'Bu gün', yesterday: 'Dünən' };

    expect(formatDateTime(new Date(2026, 8, 27, 14, 20), words, now)).toBe('Bu gün 14:20');
    expect(formatDateTime(new Date(2026, 8, 26, 9, 5), words, now)).toBe('Dünən 09:05');
    expect(formatDateTime(new Date(2026, 8, 25, 9, 5), words, now)).toBe('25.09.2026 09:05');
  });

  it('writes a time as HH:mm', () => {
    expect(formatTime(new Date(2026, 8, 27, 18, 3))).toBe('18:03');
    expect(formatTime(null)).toBe('—');
  });

  it('writes sizes in the largest unit that keeps them readable', () => {
    expect(formatBytes(512)).toBe('512\u00a0B');
    expect(formatBytes(1536)).toBe('1,5\u00a0KB');
    expect(formatBytes(1536, 'en')).toBe('1.5\u00a0KB');
    expect(formatBytes(12 * 1024 * 1024)).toBe('12\u00a0MB');
    expect(formatBytes(undefined)).toBe('—');
  });

  it('groups the thousands of a count by language', () => {
    expect(formatCount(12480)).toBe('12\u00a0480');
    expect(formatCount(12480, 'en')).toBe('12,480');
    expect(formatCount(7)).toBe('7');
  });
});
