import { TestBed } from '@angular/core/testing';
import { DateAdapter } from '@angular/material/core';
import { MatDatepickerIntl } from '@angular/material/datepicker';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { beforeEach, describe, expect, it } from 'vitest';

import { supportedLanguages, translations } from '../core/i18n/translations';
import { neoDateFormats, provideNeoDates } from './date-input';

describe('date fields', () => {
  let adapter: DateAdapter<Date>;
  let translate: TranslateService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideTranslateService({ fallbackLang: 'az', lang: 'az' }), provideNeoDates()],
    });

    translate = TestBed.inject(TranslateService);

    for (const language of supportedLanguages) {
      translate.setTranslation(language, translations[language]);
    }

    translate.use('az');
    adapter = TestBed.inject(DateAdapter);
  });

  it('reads and writes dd.MM.yyyy whatever the language', () => {
    for (const language of supportedLanguages) {
      translate.use(language);

      const date = adapter.parse('07.09.2026', neoDateFormats.parse.dateInput)!;

      expect([date.getDate(), date.getMonth(), date.getFullYear()]).toEqual([7, 8, 2026]);
      expect(adapter.format(date, neoDateFormats.display.dateInput)).toBe('07.09.2026');
    }
  });

  it('refuses other formats and impossible days instead of guessing', () => {
    for (const typed of ['09/07/2026', '2026-09-07', '31.02.2026', '7.9']) {
      const date = adapter.parse(typed, neoDateFormats.parse.dateInput);

      expect(adapter.isValid(date!), typed).toBe(false);
    }

    expect(adapter.parse('', neoDateFormats.parse.dateInput)).toBeNull();
  });

  // Not Intl: Chrome has no Azerbaijani date names and showed "2026 M09".
  it('starts the week on Monday and names months and days from the translations', () => {
    const september = new Date(2026, 8, 1);
    const header = neoDateFormats.display.monthYearLabel;

    expect(adapter.getFirstDayOfWeek()).toBe(1);
    expect(adapter.format(september, header)).toBe('Sentyabr 2026');
    expect(adapter.getDayOfWeekNames('long')[1]).toBe('Bazar ertəsi');
    expect(adapter.getMonthNames('short')).toHaveLength(12);

    translate.use('ru');
    expect(adapter.format(september, header)).toBe('Сентябрь 2026');
    expect(adapter.getDayOfWeekNames('narrow')).toEqual(['Вс', 'Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб']);

    translate.use('en');
    expect(adapter.format(september, header)).toBe('September 2026');
    expect(adapter.getDayOfWeekNames('long')).toHaveLength(7);
  });

  it('translates the calendar buttons and follows a language switch', () => {
    const intl = TestBed.inject(MatDatepickerIntl);

    expect(intl.nextMonthLabel).toBe('Növbəti ay');

    translate.use('en');
    expect(intl.nextMonthLabel).toBe('Next month');
  });
});
