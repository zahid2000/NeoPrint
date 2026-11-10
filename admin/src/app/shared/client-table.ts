import { Signal, computed, signal } from '@angular/core';
import { Sort } from '@angular/material/sort';

/** A row's value a column sorts by. */
export type SortValue = string | number | boolean | Date | null | undefined;

/** How many rows a page of a list shows (docs/DESIGN_SYSTEM.md section 5.3). */
export const clientPageSize = 20;

/**
 * Sorting and pages for a list the panel has whole (T-424): kiosks, printers,
 * devices, admins... The server sends these in one answer; the table sorts by
 * the column header pressed and shows twenty rows a page. A list the server
 * pages itself is sorted there, not here - a page sorted alone would lie.
 */
export class ClientTable<T> {
  readonly sort = signal<Sort>({ active: '', direction: '' });
  readonly page = signal(1);

  readonly total = computed(() => this.source().length);

  /** The rows in the order asked, all of them. */
  readonly sorted = computed(() => {
    const rows = [...this.source()];
    const { active, direction } = this.sort();
    const by = this.columns[active];
    if (!by || direction === '') {
      return rows;
    }
    const sign = direction === 'asc' ? 1 : -1;
    return rows.sort((a, b) => sign * compare(by(a), by(b)));
  });

  /** The rows of the page shown. */
  readonly rows = computed(() => {
    const pages = Math.max(1, Math.ceil(this.total() / this.pageSize));
    const page = Math.min(this.page(), pages);
    return this.sorted().slice((page - 1) * this.pageSize, page * this.pageSize);
  });

  constructor(
    private readonly source: Signal<readonly T[]>,
    private readonly columns: Readonly<Record<string, (row: T) => SortValue>>,
    readonly pageSize = clientPageSize,
  ) {}

  /** A header pressed: sorted by it, from the first page. */
  onSort(sort: Sort): void {
    this.sort.set(sort);
    this.page.set(1);
  }

  go(page: number): void {
    this.page.set(page);
  }

  /** The filters changed: back to the first page. */
  first(): void {
    this.page.set(1);
  }
}

/** Empty last, words as a person reads them (az, numbers by value), dates by time. */
function compare(a: SortValue, b: SortValue): number {
  const empty = (value: SortValue) => value === null || value === undefined || value === '';
  if (empty(a) || empty(b)) {
    return empty(a) === empty(b) ? 0 : empty(a) ? 1 : -1;
  }
  if (a instanceof Date || b instanceof Date) {
    return new Date(a as Date).getTime() - new Date(b as Date).getTime();
  }
  if (typeof a === 'number' && typeof b === 'number') {
    return a - b;
  }
  if (typeof a === 'boolean' && typeof b === 'boolean') {
    return Number(a) - Number(b);
  }
  return String(a).localeCompare(String(b), 'az', { numeric: true, sensitivity: 'base' });
}
