import { Injector, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';

import { rememberFilters } from './remembered-filters';

/** T-424: back on a page, its filters are as they were left. */
describe('rememberFilters', () => {
  afterEach(() => localStorage.clear());

  function fields() {
    return {
      code: signal(''),
      statuses: signal<string[]>([]),
      kiosk: signal<string | null>(null),
      from: signal<Date | null>(null),
      failed: signal(false),
    };
  }

  it('keeps what was chosen and gives it back, dates as dates', () => {
    const injector = TestBed.inject(Injector);
    const first = fields();
    rememberFilters('test', first, { injector, dates: ['from'] });
    first.code.set('A1B2');
    first.statuses.set(['Queued']);
    first.kiosk.set('k-1');
    first.from.set(new Date(Date.UTC(2026, 10, 1)));
    first.failed.set(true);
    TestBed.tick();

    const again = fields();
    rememberFilters('test', again, { injector, dates: ['from'] });

    expect(again.code()).toBe('A1B2');
    expect(again.statuses()).toEqual(['Queued']);
    expect(again.kiosk()).toBe('k-1');
    expect(again.from()).toEqual(new Date(Date.UTC(2026, 10, 1)));
    expect(again.failed()).toBe(true);
  });

  it('ignores a kept value of the wrong shape and a broken record', () => {
    const injector = TestBed.inject(Injector);
    localStorage.setItem(
      'neo.filterValues.shape',
      JSON.stringify({ code: 5, statuses: 'Queued', from: 'yesterday', failed: 'yes' }),
    );
    const restored = fields();
    rememberFilters('shape', restored, { injector, dates: ['from'] });
    expect(restored.code()).toBe('');
    expect(restored.statuses()).toEqual([]);
    expect(restored.from()).toBeNull();
    expect(restored.failed()).toBe(false);

    localStorage.setItem('neo.filterValues.broken', '{not json');
    const fresh = fields();
    rememberFilters('broken', fresh, { injector });
    expect(fresh.code()).toBe('');
  });
});
