import { Injectable, Provider, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  DateAdapter,
  MAT_DATE_FORMATS,
  MatDateFormats,
  NativeDateAdapter,
} from '@angular/material/core';
import { MatDatepickerIntl } from '@angular/material/datepicker';
import { TranslateService } from '@ngx-translate/core';

/** The format name the adapter turns into `dd.MM.yyyy`. */
const typedFormat = 'dd.MM.yyyy';

/** The format name for the calendar header: "Sentyabr 2026". */
const monthYearFormat = 'MMMM yyyy';

const typedDate = /^(\d{1,2})\.(\d{1,2})\.(\d{4})$/;

const pad = (value: number): string => value.toString().padStart(2, '0');

/**
 * Dates typed and shown as `dd.MM.yyyy` in every language
 * (docs/DESIGN_SYSTEM.md section 6, "Tarix sahəsi"), whatever the browser's
 * own language is; the calendar follows the panel's language and starts on
 * Monday. Month and day names come from the translation files, not Intl:
 * Chrome's ICU data has no Azerbaijani dates and falls back to "2026 M09"
 * and English day names.
 */
@Injectable()
export class NeoDateAdapter extends NativeDateAdapter {
  private readonly translate = inject(TranslateService);

  constructor() {
    super();
    this.follow(this.translate.getCurrentLang());
    this.translate.onLangChange
      .pipe(takeUntilDestroyed())
      .subscribe(({ lang }) => this.follow(lang));
  }

  override getFirstDayOfWeek(): number {
    return 1;
  }

  override getMonthNames(style: 'long' | 'short' | 'narrow'): string[] {
    return this.names(style === 'long' ? 'date.monthsLong' : 'date.monthsShort');
  }

  /** Sunday first, as the adapter contract has it; the calendar starts the week on Monday. */
  override getDayOfWeekNames(style: 'long' | 'short' | 'narrow'): string[] {
    return this.names(style === 'long' ? 'date.daysLong' : 'date.daysShort');
  }

  override format(date: Date, displayFormat: object | string): string {
    if (!this.isValid(date)) {
      throw new Error('NeoDateAdapter: cannot format an invalid date.');
    }

    if (displayFormat === typedFormat) {
      return `${pad(date.getDate())}.${pad(date.getMonth() + 1)}.${date.getFullYear()}`;
    }

    if (displayFormat === monthYearFormat) {
      return `${this.getMonthNames('long')[date.getMonth()]} ${date.getFullYear()}`;
    }

    return super.format(date, displayFormat as object);
  }

  /** `dd.MM.yyyy` only; anything else is an invalid date the field reports, never a guess. */
  override parse(value: unknown): Date | null {
    if (value instanceof Date) {
      return value;
    }

    if (typeof value !== 'string' || value.trim() === '') {
      return null;
    }

    const match = typedDate.exec(value.trim());

    if (!match) {
      return this.invalid();
    }

    const [day, month, year] = [Number(match[1]), Number(match[2]), Number(match[3])];
    const date = new Date(year, month - 1, day);

    // 31.02.2026 would roll over to March; it is a typing mistake instead.
    return date.getFullYear() === year && date.getMonth() === month - 1 && date.getDate() === day
      ? date
      : this.invalid();
  }

  /** A comma-separated list from the translation files. */
  private names(key: string): string[] {
    return (this.translate.instant(key) as string).split(',').map((name) => name.trim());
  }

  /** Setting the locale is what tells an open calendar to redraw with the new names. */
  private follow(language: string | undefined): void {
    this.setLocale(language ?? 'az');
  }
}

export const neoDateFormats: MatDateFormats = {
  parse: { dateInput: typedFormat },
  display: {
    dateInput: typedFormat,
    monthYearLabel: monthYearFormat,
    dateA11yLabel: typedFormat,
    monthYearA11yLabel: monthYearFormat,
  },
};

/** The calendar's button names and labels in the panel's language. */
@Injectable()
export class NeoDatepickerIntl extends MatDatepickerIntl {
  private readonly translate = inject(TranslateService);

  constructor() {
    super();
    this.update();
    this.translate.onLangChange.pipe(takeUntilDestroyed()).subscribe(() => this.update());
  }

  override formatYearRangeLabel(start: string, end: string): string {
    return this.translate.instant('date.yearRange', { start, end });
  }

  private update(): void {
    const t = (key: string) => this.translate.instant(key);

    this.calendarLabel = t('date.calendar');
    this.openCalendarLabel = t('date.openCalendar');
    this.closeCalendarLabel = t('date.closeCalendar');
    this.prevMonthLabel = t('date.prevMonth');
    this.nextMonthLabel = t('date.nextMonth');
    this.prevYearLabel = t('date.prevYear');
    this.nextYearLabel = t('date.nextYear');
    this.prevMultiYearLabel = t('date.prevYears');
    this.nextMultiYearLabel = t('date.nextYears');
    this.switchToMonthViewLabel = t('date.chooseDate');
    this.switchToMultiYearViewLabel = t('date.chooseMonthYear');
    this.startDateLabel = t('date.start');
    this.endDateLabel = t('date.end');
    this.changes.next();
  }
}

/**
 * The adapter, the formats and the calendar's text, for the `providers` of a
 * component with a date field. Kept off the root injector so the datepicker
 * stays in the lazy chunks of the pages that use it.
 */
export function provideNeoDates(): Provider[] {
  return [
    { provide: DateAdapter, useClass: NeoDateAdapter },
    { provide: MAT_DATE_FORMATS, useValue: neoDateFormats },
    { provide: MatDatepickerIntl, useClass: NeoDatepickerIntl },
  ];
}
