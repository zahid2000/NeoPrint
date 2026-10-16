import { inject, Pipe, PipeTransform } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';

/**
 * The formats docs/DESIGN_SYSTEM.md section 6 fixes for every screen, in one
 * place so that no template formats money or a date its own way. The same
 * rules are implemented for the mobile app (lib/core/format/formats.dart) and
 * the kiosk (NeoPrint.Kiosk.Localization.Formats).
 *
 * Dates read the same in every language - `27.09.2026 18:33`, 24-hour - with
 * "today" and "yesterday" as words of the viewer's language. Numbers follow the
 * language: a decimal comma and spaced thousands in Azerbaijani and Russian, a
 * decimal point and comma thousands in English.
 */

export type FormatLanguage = 'az' | 'en' | 'ru';

interface Separators {
  readonly decimal: string;
  readonly group: string;
}

const separators: Record<FormatLanguage, Separators> = {
  az: { decimal: ',', group: '\u00a0' },
  ru: { decimal: ',', group: '\u00a0' },
  en: { decimal: '.', group: ',' },
};

const separatorsFor = (language: string | undefined): Separators =>
  separators[(language ?? 'az') as FormatLanguage] ?? separators.az;

const pad = (value: number): string => value.toString().padStart(2, '0');

const group = (digits: string, separator: string): string =>
  digits.replace(/\B(?=(\d{3})+(?!\d))/g, separator);

/** `1 240,00 ₼` (az, ru) or `1,240.00 ₼` (en). */
export function formatMoney(amount: number | null | undefined, language?: string): string {
  if (amount === null || amount === undefined || Number.isNaN(amount)) {
    return '—';
  }

  const { decimal, group: thousands } = separatorsFor(language);
  const [whole, fraction] = Math.abs(amount).toFixed(2).split('.');

  return `${amount < 0 ? '−' : ''}${group(whole, thousands)}${decimal}${fraction}\u00a0₼`;
}

/** The words for today and yesterday, in the viewer's language. */
export interface RelativeDayWords {
  readonly today: string;
  readonly yesterday: string;
}

/**
 * `27.09.2026 14:20` in the viewer's time zone; `Bu gün 14:20` / `Dünən 14:20`
 * when `words` are given. `now` is for tests.
 */
export function formatDateTime(
  value: string | Date | null | undefined,
  words?: RelativeDayWords,
  now: Date = new Date(),
): string {
  if (!value) {
    return '—';
  }

  const date = value instanceof Date ? value : new Date(value);

  if (Number.isNaN(date.getTime())) {
    return '—';
  }

  const time = `${pad(date.getHours())}:${pad(date.getMinutes())}`;

  if (words) {
    const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
    const days = Math.round((startOfDay(now) - startOfDay(date)) / 86_400_000);

    if (days === 0) {
      return `${words.today} ${time}`;
    }

    if (days === 1) {
      return `${words.yesterday} ${time}`;
    }
  }

  return `${pad(date.getDate())}.${pad(date.getMonth() + 1)}.${date.getFullYear()} ${time}`;
}

/** `18:33`, 24-hour, in the viewer's time zone. */
export function formatTime(value: string | Date | null | undefined): string {
  if (!value) {
    return '—';
  }

  const date = value instanceof Date ? value : new Date(value);

  return Number.isNaN(date.getTime()) ? '—' : `${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/** `18:33:07`: a time read live, where the seconds show that it moved. */
export function formatClock(value: string | Date | null | undefined): string {
  if (!value) {
    return '—';
  }

  const date = value instanceof Date ? value : new Date(value);

  return Number.isNaN(date.getTime())
    ? '—'
    : `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

/** `12,5 MB` (az, ru) or `12.5 MB` (en). */
export function formatBytes(bytes: number | null | undefined, language?: string): string {
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

  return `${text.replace('.', separatorsFor(language).decimal)}\u00a0${units[unit]}`;
}

/** A plain number with grouped thousands: `12 480` (az, ru) or `12,480` (en). */
export function formatCount(value: number | null | undefined, language?: string): string {
  if (value === null || value === undefined) {
    return '—';
  }

  return group(Math.round(value).toString(), separatorsFor(language).group);
}

/**
 * The pipes read the current language on every change detection, so a switch
 * in the header changes every number and date on the page at once - hence not
 * pure. The work is a few string operations.
 */
abstract class LanguageAwarePipe {
  protected readonly translate = inject(TranslateService);

  protected get language(): string {
    return this.translate.getCurrentLang() ?? 'az';
  }
}

@Pipe({ name: 'money', pure: false })
export class MoneyPipe extends LanguageAwarePipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return formatMoney(value, this.language);
  }
}

@Pipe({ name: 'dateTime', pure: false })
export class DateTimePipe extends LanguageAwarePipe implements PipeTransform {
  transform(value: string | Date | null | undefined): string {
    return formatDateTime(value, {
      today: this.translate.instant('common.today'),
      yesterday: this.translate.instant('common.yesterday'),
    });
  }
}

@Pipe({ name: 'time' })
export class TimePipe implements PipeTransform {
  transform(value: string | Date | null | undefined): string {
    return formatTime(value);
  }
}

@Pipe({ name: 'bytes', pure: false })
export class BytesPipe extends LanguageAwarePipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return formatBytes(value, this.language);
  }
}

@Pipe({ name: 'count', pure: false })
export class CountPipe extends LanguageAwarePipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return formatCount(value, this.language);
  }
}
