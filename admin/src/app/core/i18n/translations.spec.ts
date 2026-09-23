import { describe, expect, it } from 'vitest';

import {
  defaultLanguage,
  supportedLanguages,
  translations,
} from './translations';

describe('translations', () => {
  it('defaults to Azerbaijani (plan section 2.1)', () => {
    expect(defaultLanguage).toBe('az');
  });

  it('carries a dictionary for every supported language', () => {
    for (const language of supportedLanguages) {
      expect(Object.keys(translations[language]).length).toBeGreaterThan(0);
    }
  });

  it('translates the same keys in every language', () => {
    const reference = Object.keys(translations.az).sort();

    for (const language of supportedLanguages) {
      const keys = Object.keys(translations[language]).sort();

      // A key present in one language and missing in another shows up in the
      // interface as the raw key, which is how untranslated text ships.
      expect(keys, `${language} differs from az`).toEqual(reference);
    }
  });

  it('leaves no value empty', () => {
    for (const language of supportedLanguages) {
      for (const [key, value] of Object.entries(translations[language])) {
        expect(value.trim(), `${language}.${key}`).not.toBe('');
      }
    }
  });

  it('uses the same interpolation placeholders across languages', () => {
    const placeholders = (text: string) =>
      [...text.matchAll(/\{\{\s*(\w+)\s*\}\}/g)].map((match) => match[1]).sort();

    for (const [key, azText] of Object.entries(translations.az)) {
      const expected = placeholders(azText);

      for (const language of supportedLanguages) {
        expect(
          placeholders(translations[language][key]),
          `${language}.${key}`,
        ).toEqual(expected);
      }
    }
  });
});
