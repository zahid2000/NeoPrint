/// The formats docs/DESIGN_SYSTEM.md section 6 fixes for every screen, in one
/// place so no widget formats a date, a size or money its own way. The admin
/// panel (shared/format.pipes.ts) and the kiosk (Localization/Formats.cs)
/// follow the same table: dates read the same in every language, numbers take
/// a decimal comma in az and ru and a decimal point in en.
library;

/// Decimal and thousands separators for a language code.
({String decimal, String group}) _separators(String language) =>
    language == 'en'
    ? (decimal: '.', group: ',')
    : (decimal: ',', group: '\u00a0');

String _grouped(String digits, String separator) =>
    digits.replaceAllMapped(RegExp(r'\B(?=(\d{3})+(?!\d))'), (_) => separator);

/// `1 240,00 ₼` (az, ru) or `1,240.00 ₼` (en).
String formatMoney(num amount, {String language = 'az'}) {
  final separators = _separators(language);
  final parts = amount.abs().toStringAsFixed(2).split('.');
  final sign = amount < 0 ? '−' : '';

  return '$sign${_grouped(parts[0], separators.group)}'
      '${separators.decimal}${parts[1]}\u00a0₼';
}

String _two(int value) => value.toString().padLeft(2, '0');

/// `27.09.2026 14:20`; today and yesterday as words ("Bu gün 14:20").
///
/// [today] and [yesterday] are the translated words; [now] is for tests.
String formatDateTime(
  DateTime value, {
  required String today,
  required String yesterday,
  DateTime? now,
}) {
  final local = value.toLocal();
  final current = (now ?? DateTime.now()).toLocal();
  final time = '${_two(local.hour)}:${_two(local.minute)}';

  final day = DateTime(local.year, local.month, local.day);
  final todayStart = DateTime(current.year, current.month, current.day);

  if (day == todayStart) {
    return '$today $time';
  }

  if (day == todayStart.subtract(const Duration(days: 1))) {
    return '$yesterday $time';
  }

  return '${_two(local.day)}.${_two(local.month)}.${local.year} $time';
}

/// `12,5 MB` (az, ru) or `12.5 MB` (en): the largest unit that keeps the
/// number readable.
String formatBytes(int bytes, {String language = 'az'}) {
  const units = ['B', 'KB', 'MB', 'GB'];
  var value = bytes.toDouble();
  var unit = 0;

  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }

  final text = unit == 0
      ? value.toStringAsFixed(0)
      : value.toStringAsFixed(value < 10 ? 1 : 0);

  return '${text.replaceAll('.', _separators(language).decimal)}\u00a0${units[unit]}';
}

/// A distance as its number and unit: `850` m under a kilometre, `1,2` km
/// up to ten, then whole kilometres. The unit word is the caller's (it is
/// translated: km / км).
({String value, bool kilometres}) formatDistance(
  double km, {
  String language = 'az',
}) {
  final decimal = _separators(language).decimal;

  if (km < 1) {
    // Rounded to tens of metres: the search point is a phone's fix, not a survey.
    return (value: ((km * 100).round() * 10).toString(), kilometres: false);
  }

  return (
    value: km < 10
        ? km.toStringAsFixed(1).replaceAll('.', decimal)
        : km.round().toString(),
    kilometres: true,
  );
}

/// A percentage or factor without trailing zeros: `10`, `12,5` (az, ru) or
/// `12.5` (en).
String formatNumber(double value, {String language = 'az'}) {
  final text = value == value.roundToDouble()
      ? value.round().toString()
      : value.toStringAsFixed(2).replaceFirst(RegExp(r'\.?0+$'), '');

  return text.replaceAll('.', _separators(language).decimal);
}
