import { describe, expect, it } from 'vitest';

import { defaultLanguage, supportedLanguages, translations } from './translations';

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
        expect(placeholders(translations[language][key]), `${language}.${key}`).toEqual(expected);
      }
    }
  });

  // The checks below catch text that is in the wrong language rather than
  // missing - the case the parity checks above cannot see ("Enrollment token
  // yarat" passed all of them). The same rules run on the mobile ARB files
  // and the kiosk catalogue (docs/DESIGN_SYSTEM.md section 6).

  /** Words that are the same in every language: brands, abbreviations, units. */
  const sameInEveryLanguage = new Set([
    'NeoPrint',
    'NeoPrint admin',
    'Email',
    'PDF',
    'QR',
    'PIN',
    'A4',
    'A3',
    'AZ',
    'EN',
    'RU',
    'Azərbaycan',
    'English',
    'Kiosk',
    'Printer',
    'Status',
    'Region',
    'Admin PIN',
    '1–2048 MB',
    'Русский',
    'S3',
    'RustFS',
    'MinIO',
    'Scality RING',
    'OpenStreetMap',
  ]);

  /** English words that have Azerbaijani equivalents and must not leak into az. */
  const englishInAzerbaijani =
    /\b(token|tokens|flag|flags|feature|features|enrollment|dashboard|default|override|overrides|rollout|provisioning|endpoint|mirror|primary|secondary|hash|job|jobs|queue|upload|download|error|network)\b/i;

  const cyrillic = /[Ѐ-ӿ]/;
  const azerbaijaniLetters = /[əƏğĞıİşŞçÇöÖüÜ]/;

  /** Text with its placeholders removed, so {{count}} does not count as a word. */
  const words = (text: string) => text.replace(/\{\{\s*\w+\s*\}\}/g, '').trim();

  // Each check lists every offender at once, so one run shows all there is to fix.

  it('writes Azerbaijani text in Azerbaijani', () => {
    const offenders = Object.entries(translations.az)
      .filter(([, value]) => englishInAzerbaijani.test(value) || cyrillic.test(value))
      .map(([key, value]) => `${key}: ${value}`);

    expect(offenders).toEqual([]);
  });

  it('does not leave the Azerbaijani text as the English one', () => {
    const offenders = Object.entries(translations.az)
      .filter(([key, value]) => {
        const text = words(value);
        return (
          text.length >= 4 && !sameInEveryLanguage.has(text) && text === words(translations.en[key])
        );
      })
      .map(([key, value]) => `${key}: ${value}`);

    expect(offenders).toEqual([]);
  });

  it('writes Russian text in Cyrillic', () => {
    const offenders = Object.entries(translations.ru)
      .filter(([, value]) => {
        const letters = words(value).replace(/[^\p{L}]/gu, '');
        return (
          letters.length >= 3 && !sameInEveryLanguage.has(words(value)) && !cyrillic.test(value)
        );
      })
      .map(([key, value]) => `${key}: ${value}`);

    expect(offenders).toEqual([]);
  });

  it('keeps Azerbaijani and Russian letters out of English', () => {
    const offenders = Object.entries(translations.en)
      .filter(
        ([, value]) =>
          !sameInEveryLanguage.has(value) &&
          (cyrillic.test(value) || azerbaijaniLetters.test(value)),
      )
      .map(([key, value]) => `${key}: ${value}`);

    expect(offenders).toEqual([]);
  });
});
