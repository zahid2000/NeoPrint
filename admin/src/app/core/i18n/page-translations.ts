/**
 * One page's interface text in every supported language.
 *
 * Pages keep their text in files of their own, merged into the one dictionary
 * in translations.ts: the parity checks in translations.spec.ts cover them all
 * the same, and no single file grows past reading.
 */
export type PageTranslations = Readonly<Record<'az' | 'en' | 'ru', Readonly<Record<string, string>>>>;
