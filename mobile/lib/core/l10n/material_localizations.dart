import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:intl/intl.dart' as intl;

import '../../l10n/app_localizations.dart';

/// Material's own strings and formats, with one change: the weekday row of the
/// date picker comes from our ARB files (`weekdayShort*`).
///
/// Flutter takes that row from CLDR's narrow weekdays, which for Azerbaijani are
/// the digits 1–7. Every other string and format is Flutter's, built exactly as
/// [GlobalMaterialLocalizations.delegate] builds them.
class AppMaterialLocalizationsDelegate
    extends LocalizationsDelegate<MaterialLocalizations> {
  const AppMaterialLocalizationsDelegate();

  @override
  bool isSupported(Locale locale) =>
      AppLocalizations.delegate.isSupported(locale);

  @override
  Future<MaterialLocalizations> load(Locale locale) =>
      // Flutter's own load first: it loads the date symbols the formats need.
      // Both are synchronous futures, so the first frame is already localized.
      GlobalMaterialLocalizations.delegate
          .load(locale)
          .then((_) => _build(locale));

  MaterialLocalizations _build(Locale locale) {
    final name = locale.languageCode;
    final l10n = lookupAppLocalizations(locale);
    final weekdays = [
      l10n.weekdayShortSun,
      l10n.weekdayShortMon,
      l10n.weekdayShortTue,
      l10n.weekdayShortWed,
      l10n.weekdayShortThu,
      l10n.weekdayShortFri,
      l10n.weekdayShortSat,
    ];
    final fullYearFormat = intl.DateFormat.y(name);
    final compactDateFormat = intl.DateFormat.yMd(name);
    final shortDateFormat = intl.DateFormat.yMMMd(name);
    final mediumDateFormat = intl.DateFormat.MMMEd(name);
    final longDateFormat = intl.DateFormat.yMMMMEEEEd(name);
    final yearMonthFormat = intl.DateFormat.yMMMM(name);
    final shortMonthDayFormat = intl.DateFormat.MMMd(name);
    final decimalFormat = intl.NumberFormat.decimalPattern(name);
    final twoDigitZeroPaddedFormat = intl.NumberFormat('00', name);

    final MaterialLocalizations localizations = switch (name) {
      'az' => _Az(
        weekdays,
        fullYearFormat: fullYearFormat,
        compactDateFormat: compactDateFormat,
        shortDateFormat: shortDateFormat,
        mediumDateFormat: mediumDateFormat,
        longDateFormat: longDateFormat,
        yearMonthFormat: yearMonthFormat,
        shortMonthDayFormat: shortMonthDayFormat,
        decimalFormat: decimalFormat,
        twoDigitZeroPaddedFormat: twoDigitZeroPaddedFormat,
      ),
      'ru' => _Ru(
        weekdays,
        fullYearFormat: fullYearFormat,
        compactDateFormat: compactDateFormat,
        shortDateFormat: shortDateFormat,
        mediumDateFormat: mediumDateFormat,
        longDateFormat: longDateFormat,
        yearMonthFormat: yearMonthFormat,
        shortMonthDayFormat: shortMonthDayFormat,
        decimalFormat: decimalFormat,
        twoDigitZeroPaddedFormat: twoDigitZeroPaddedFormat,
      ),
      _ => _En(
        weekdays,
        fullYearFormat: fullYearFormat,
        compactDateFormat: compactDateFormat,
        shortDateFormat: shortDateFormat,
        mediumDateFormat: mediumDateFormat,
        longDateFormat: longDateFormat,
        yearMonthFormat: yearMonthFormat,
        shortMonthDayFormat: shortMonthDayFormat,
        decimalFormat: decimalFormat,
        twoDigitZeroPaddedFormat: twoDigitZeroPaddedFormat,
      ),
    };
    return localizations;
  }

  @override
  bool shouldReload(AppMaterialLocalizationsDelegate old) => false;
}

/// Sunday first, as [MaterialLocalizations.narrowWeekdays] expects.
mixin _ArbWeekdays on GlobalMaterialLocalizations {
  List<String> get _weekdays;

  @override
  List<String> get narrowWeekdays => _weekdays;
}

class _Az extends MaterialLocalizationAz with _ArbWeekdays {
  const _Az(
    this._weekdays, {
    required super.fullYearFormat,
    required super.compactDateFormat,
    required super.shortDateFormat,
    required super.mediumDateFormat,
    required super.longDateFormat,
    required super.yearMonthFormat,
    required super.shortMonthDayFormat,
    required super.decimalFormat,
    required super.twoDigitZeroPaddedFormat,
  });

  @override
  final List<String> _weekdays;
}

class _En extends MaterialLocalizationEn with _ArbWeekdays {
  const _En(
    this._weekdays, {
    required super.fullYearFormat,
    required super.compactDateFormat,
    required super.shortDateFormat,
    required super.mediumDateFormat,
    required super.longDateFormat,
    required super.yearMonthFormat,
    required super.shortMonthDayFormat,
    required super.decimalFormat,
    required super.twoDigitZeroPaddedFormat,
  });

  @override
  final List<String> _weekdays;
}

class _Ru extends MaterialLocalizationRu with _ArbWeekdays {
  const _Ru(
    this._weekdays, {
    required super.fullYearFormat,
    required super.compactDateFormat,
    required super.shortDateFormat,
    required super.mediumDateFormat,
    required super.longDateFormat,
    required super.yearMonthFormat,
    required super.shortMonthDayFormat,
    required super.decimalFormat,
    required super.twoDigitZeroPaddedFormat,
  });

  @override
  final List<String> _weekdays;
}
