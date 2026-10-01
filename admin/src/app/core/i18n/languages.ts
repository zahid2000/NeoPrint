/**
 * The panel's languages (plan section 2.1), apart from the dictionary itself:
 * whatever starts the app needs these, and importing them must not pull every
 * translation into the first download (translations.ts is loaded on its own).
 */
export const supportedLanguages = ['az', 'en', 'ru'] as const;

export type SupportedLanguage = (typeof supportedLanguages)[number];

export const defaultLanguage: SupportedLanguage = 'az';

export const languageNames: Record<SupportedLanguage, string> = {
  az: 'Azərbaycan',
  en: 'English',
  ru: 'Русский',
};
