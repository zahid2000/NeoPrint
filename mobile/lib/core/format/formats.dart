/// The formats docs/DESIGN_SYSTEM.md section 6 fixes for every screen, in one
/// place so no widget formats a date or a size its own way.
library;

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

/// `12,5 MB`: the largest unit that keeps the number readable, with the same
/// decimal comma as money.
String formatBytes(int bytes) {
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

  return '${text.replaceAll('.', ',')} ${units[unit]}';
}
