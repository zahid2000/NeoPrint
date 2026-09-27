import { describe, expect, it } from 'vitest';

import { formatBytes, formatCount, formatDateTime, formatMoney } from './format.pipes';

/** docs/DESIGN_SYSTEM.md section 6, as it is written there. */
describe('format pipes', () => {
  it('writes money with a decimal comma and spaced thousands', () => {
    expect(formatMoney(12.5)).toBe('12,50\u00a0₼');
    expect(formatMoney(1240)).toBe('1\u00a0240,00\u00a0₼');
    expect(formatMoney(0.1)).toBe('0,10\u00a0₼');
    expect(formatMoney(-3)).toBe('−3,00\u00a0₼');
    expect(formatMoney(null)).toBe('—');
  });

  it('writes dates as dd.MM.yyyy HH:mm', () => {
    expect(formatDateTime(new Date(2026, 8, 7, 4, 5))).toBe('07.09.2026 04:05');
    expect(formatDateTime(undefined)).toBe('—');
    expect(formatDateTime('not a date')).toBe('—');
  });

  it('writes sizes in the largest unit that keeps them readable', () => {
    expect(formatBytes(512)).toBe('512\u00a0B');
    expect(formatBytes(1536)).toBe('1,5\u00a0KB');
    expect(formatBytes(12 * 1024 * 1024)).toBe('12\u00a0MB');
    expect(formatBytes(undefined)).toBe('—');
  });

  it('spaces the thousands of a count', () => {
    expect(formatCount(12480)).toBe('12\u00a0480');
    expect(formatCount(7)).toBe('7');
  });
});
