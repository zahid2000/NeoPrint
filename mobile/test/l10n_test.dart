import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// The translations are in the language they claim to be
/// (docs/DESIGN_SYSTEM.md section 6).
///
/// The same rules run on the admin panel (translations.spec.ts) and on the
/// kiosk (StringsQualityTests): every key in every language, the same
/// placeholders, Russian in Cyrillic, no Azerbaijani or Russian letters in
/// English, and no English left in Azerbaijani.
void main() {
  Map<String, String> load(String language) {
    final json = jsonDecode(
      File('lib/l10n/app_$language.arb').readAsStringSync(),
    ) as Map<String, dynamic>;

    return {
      for (final entry in json.entries)
        if (!entry.key.startsWith('@')) entry.key: entry.value as String,
    };
  }

  final az = load('az');
  final en = load('en');
  final ru = load('ru');

  // Brands, abbreviations and words Azerbaijani shares with English.
  const sameInEveryLanguage = {
    'NeoPrint',
    'Email',
    'PDF',
    'QR',
    'USB',
    'PIN',
    'OK',
    'Kiosk',
    'Azərbaycan',
    'English',
    'Русский',
  };

  final englishInAzerbaijani = RegExp(
    r'\b(token|tokens|flag|flags|feature|features|enrollment|dashboard|default|override|overrides|rollout|provisioning|endpoint|mirror|primary|secondary|hash|job|jobs|queue|upload|download|error|network)\b',
    caseSensitive: false,
  );
  final cyrillic = RegExp(r'[Ѐ-ӿ]');
  final azerbaijaniLetters = RegExp(r'[əƏğĞıİşŞçÇöÖüÜ]');
  final placeholder = RegExp(r'\{(\w+)\}');

  String words(String text) => text.replaceAll(placeholder, '').trim();

  List<String> placeholders(String text) =>
      placeholder.allMatches(text).map((m) => m.group(1)!).toSet().toList()
        ..sort();

  test('every language has every key, none empty', () {
    for (final (name, table) in [('en', en), ('ru', ru)]) {
      expect(table.keys.toSet(), az.keys.toSet(), reason: name);
    }

    final empty = [
      for (final (name, table) in [('az', az), ('en', en), ('ru', ru)])
        for (final entry in table.entries)
          if (entry.value.trim().isEmpty) '$name.${entry.key}',
    ];
    expect(empty, isEmpty);
  });

  test('the placeholders are the same in every language', () {
    final offenders = [
      for (final key in az.keys)
        for (final (name, table) in [('en', en), ('ru', ru)])
          if (placeholders(table[key] ?? '').join(',') !=
              placeholders(az[key]!).join(','))
            '$name.$key',
    ];
    expect(offenders, isEmpty);
  });

  test('Azerbaijani text is in Azerbaijani', () {
    final offenders = [
      for (final entry in az.entries)
        // A language's own name (the language picker) is written in it.
        if (!sameInEveryLanguage.contains(entry.value) &&
            (englishInAzerbaijani.hasMatch(entry.value) ||
                cyrillic.hasMatch(entry.value)))
          '${entry.key}: ${entry.value}',
      for (final entry in az.entries)
        if (words(entry.value).length >= 4 &&
            !sameInEveryLanguage.contains(words(entry.value)) &&
            words(entry.value) == words(en[entry.key] ?? ''))
          '${entry.key} is the English text',
    ];
    expect(offenders, isEmpty);
  });

  test('Russian text is in Cyrillic', () {
    final offenders = [
      for (final entry in ru.entries)
        if (words(entry.value)
                    .replaceAll(RegExp(r'[^\p{L}]', unicode: true), '')
                    .length >=
                3 &&
            !sameInEveryLanguage.contains(words(entry.value)) &&
            !cyrillic.hasMatch(entry.value))
          '${entry.key}: ${entry.value}',
    ];
    expect(offenders, isEmpty);
  });

  test('English text has no Azerbaijani or Russian letters', () {
    final offenders = [
      for (final entry in en.entries)
        if (!sameInEveryLanguage.contains(entry.value) &&
            (cyrillic.hasMatch(entry.value) ||
                azerbaijaniLetters.hasMatch(entry.value)))
          '${entry.key}: ${entry.value}',
    ];
    expect(offenders, isEmpty);
  });
}
