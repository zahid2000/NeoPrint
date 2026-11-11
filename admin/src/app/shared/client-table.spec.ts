import { signal } from '@angular/core';
import { describe, expect, it } from 'vitest';

import { ClientTable, serverSort } from './client-table';

/** T-424: a whole list sorted by a header and shown twenty rows a page. */
describe('ClientTable', () => {
  const rows = signal(
    Array.from({ length: 45 }, (_, i) => ({
      code: `KSK-${String(i + 1).padStart(4, '0')}`,
      seen: i % 3 === 0 ? null : new Date(2026, 10, 1 + i),
    })),
  );
  const make = () =>
    new ClientTable(rows, { code: (row) => row.code, seen: (row) => row.seen });

  it('shows twenty a page, and the rest on the last', () => {
    const table = make();
    expect(table.rows()).toHaveLength(20);
    table.go(3);
    expect(table.rows()).toHaveLength(5);
    expect(table.total()).toBe(45);
  });

  it('sorts by the header pressed, from the first page; empty values last either way', () => {
    const table = make();
    table.go(2);
    table.onSort({ active: 'code', direction: 'desc' });
    expect(table.page()).toBe(1);
    expect(table.rows()[0].code).toBe('KSK-0045');

    table.onSort({ active: 'seen', direction: 'asc' });
    expect(table.sorted()[0].seen).not.toBeNull();
    expect(table.sorted().at(-1)?.seen).toBeNull();

    table.onSort({ active: 'seen', direction: '' });
    // No direction: the server's order.
    expect(table.rows()[0].code).toBe('KSK-0001');
  });

  it('a page past the end after the list shrank shows its last page', () => {
    const list = signal([{ code: 'a' }, { code: 'b' }]);
    const table = new ClientTable(list, { code: (row) => row.code }, 1);
    table.go(2);
    list.set([{ code: 'a' }]);
    expect(table.rows()).toEqual([{ code: 'a' }]);
  });
});

/** T-424: a list the server pages is ordered there; the header says how. */
describe('serverSort', () => {
  it('names the column, with a minus for largest first, or nothing', () => {
    expect(serverSort({ active: 'total', direction: 'asc' })).toBe('total');
    expect(serverSort({ active: 'total', direction: 'desc' })).toBe('-total');
    expect(serverSort({ active: 'total', direction: '' })).toBeNull();
    expect(serverSort({ active: '', direction: 'asc' })).toBeNull();
  });
});
