import { Pipe, PipeTransform } from '@angular/core';

/**
 * The formats docs/DESIGN_SYSTEM.md section 6 fixes for every screen, in one
 * place so that no template formats money or a date its own way.
 */

const pad = (value: number): string => value.toString().padStart(2, '0');

/** `1 240,00 ₼`: a comma for decimals, a space between thousands. */
export function formatMoney(amount: number | null | undefined): string {
  if (amount === null || amount === undefined || Number.isNaN(amount)) {
    return '—';
  }

  const [whole, fraction] = Math.abs(amount).toFixed(2).split('.');
  const grouped = whole.replace(/\B(?=(\d{3})+(?!\d))/g, '\u00a0');

  return `${amount < 0 ? '−' : ''}${grouped},${fraction}\u00a0₼`;
}

/** `27.09.2026 14:20`, in the viewer's time zone. */
export function formatDateTime(value: string | Date | null | undefined): string {
  if (!value) {
    return '—';
  }

  const date = value instanceof Date ? value : new Date(value);

  if (Number.isNaN(date.getTime())) {
    return '—';
  }

  return `${pad(date.getDate())}.${pad(date.getMonth() + 1)}.${date.getFullYear()} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/** `12,5 MB`: sizes for people, with the same decimal comma as money. */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || bytes < 0) {
    return '—';
  }

  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;

  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }

  const text = unit === 0 ? value.toString() : value.toFixed(value < 10 ? 1 : 0);

  return `${text.replace('.', ',')}\u00a0${units[unit]}`;
}

/** A plain number with thin-space thousands: `12 480`. */
export function formatCount(value: number | null | undefined): string {
  if (value === null || value === undefined) {
    return '—';
  }

  return Math.round(value).toString().replace(/\B(?=(\d{3})+(?!\d))/g, '\u00a0');
}

@Pipe({ name: 'money' })
export class MoneyPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return formatMoney(value);
  }
}

@Pipe({ name: 'dateTime' })
export class DateTimePipe implements PipeTransform {
  transform(value: string | Date | null | undefined): string {
    return formatDateTime(value);
  }
}

@Pipe({ name: 'bytes' })
export class BytesPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return formatBytes(value);
  }
}

@Pipe({ name: 'count' })
export class CountPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return formatCount(value);
  }
}
