import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import 'schedule_models.dart';
import 'schedule_repository.dart';

/// The earliest a print can be scheduled, from now (plan section 8.2; the
/// server says the same with `schedules.tooSoon`).
const scheduleMinLead = Duration(minutes: 15);

/// How far ahead the date picker goes; the server's `Schedule.MaxDaysAhead`
/// may be shorter, and then it says so.
const _pickerDays = 365;

/// `Bu gün 14:20` / `05.10.2026 09:00`.
String scheduleTime(BuildContext context, DateTime at) {
  final l10n = AppLocalizations.of(context);
  return formatDateTime(
    at,
    today: l10n.commonToday,
    yesterday: l10n.commonYesterday,
  );
}

/// The system date and time pickers, one after the other; null when either
/// is closed, and a time sooner than [scheduleMinLead] is refused with why.
Future<DateTime?> pickScheduleTime(
  BuildContext context, {
  DateTime? initial,
}) async {
  final l10n = AppLocalizations.of(context);
  final messenger = ScaffoldMessenger.of(context);
  final now = DateTime.now();
  final earliest = now.add(scheduleMinLead);
  final start = initial != null && initial.isAfter(earliest)
      ? initial.toLocal()
      : earliest.add(const Duration(hours: 1));

  final day = await showDatePicker(
    context: context,
    initialDate: start,
    firstDate: DateTime(now.year, now.month, now.day),
    lastDate: now.add(const Duration(days: _pickerDays)),
  );

  if (day == null || !context.mounted) {
    return null;
  }

  final time = await showTimePicker(
    context: context,
    initialTime: TimeOfDay.fromDateTime(start),
  );

  if (time == null) {
    return null;
  }

  final at = DateTime(day.year, day.month, day.day, time.hour, time.minute);

  if (at.isBefore(earliest)) {
    messenger.showSnackBar(
      SnackBar(content: Text(l10n.schedTooSoon(scheduleMinLead.inMinutes))),
    );
    return null;
  }

  return at;
}

/// "Çapa ver". When the kiosk cannot take it now, the reason and an offer to
/// pick another time (plan section 9.5, step 3).
Future<void> printScheduleNow(
  BuildContext context,
  WidgetRef ref,
  ScheduledPrint item,
) async {
  final l10n = AppLocalizations.of(context);
  final messenger = ScaffoldMessenger.of(context);

  try {
    await ref.read(schedulesProvider.notifier).printNow(item.id);
    messenger.showSnackBar(
      SnackBar(
        content: Text(l10n.schedSentDone(item.kioskCode ?? '', item.jobCode)),
      ),
    );
  } on ApiException catch (error) {
    if (!context.mounted) {
      return;
    }

    final move = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        icon: Icon(Icons.print_disabled_outlined, color: context.tokens.danger),
        title: Text(l10n.schedPrintFailedTitle),
        content: Text(l10n.schedPrintFailedBody(apiErrorText(l10n, error))),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: Text(l10n.commonClose),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: Text(l10n.schedChangeTime),
          ),
        ],
      ),
    );

    if (move == true && context.mounted) {
      await rescheduleSchedule(context, ref, item);
    }
  }
}

/// "Vaxtı dəyiş" (plan section 9.5, step 4).
Future<void> rescheduleSchedule(
  BuildContext context,
  WidgetRef ref,
  ScheduledPrint item,
) async {
  final l10n = AppLocalizations.of(context);
  final messenger = ScaffoldMessenger.of(context);
  final at = await pickScheduleTime(context, initial: item.scheduledAt);

  if (at == null || !context.mounted) {
    return;
  }

  final when = scheduleTime(context, at);

  try {
    await ref.read(schedulesProvider.notifier).reschedule(item.id, at);
    messenger.showSnackBar(SnackBar(content: Text(l10n.schedMovedDone(when))));
  } on ApiException catch (error) {
    messenger.showSnackBar(
      SnackBar(
        content: Text(l10n.schedActionFailed(apiErrorText(l10n, error))),
      ),
    );
  }
}

/// Cancels it after asking; a paid one's money goes back to the wallet.
Future<void> cancelSchedule(
  BuildContext context,
  WidgetRef ref,
  ScheduledPrint item,
) async {
  final l10n = AppLocalizations.of(context);
  final messenger = ScaffoldMessenger.of(context);
  final language = Localizations.localeOf(context).languageCode;
  final paid = item.status != ScheduleStatus.pendingPayment;

  final sure = await showDialog<bool>(
    context: context,
    builder: (context) => AlertDialog(
      title: Text(l10n.schedCancelTitle),
      content: Text(
        paid
            ? l10n.schedCancelBody(formatMoney(item.amount, language: language))
            : l10n.schedCancelUnpaidBody,
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.pop(context, false),
          child: Text(l10n.commonClose),
        ),
        FilledButton(
          style: FilledButton.styleFrom(backgroundColor: context.tokens.danger),
          onPressed: () => Navigator.pop(context, true),
          child: Text(l10n.schedCancel),
        ),
      ],
    ),
  );

  if (sure != true) {
    return;
  }

  try {
    await ref.read(schedulesProvider.notifier).cancel(item.id);
    messenger.showSnackBar(SnackBar(content: Text(l10n.schedCancelledDone)));
  } on ApiException catch (error) {
    messenger.showSnackBar(
      SnackBar(
        content: Text(l10n.schedActionFailed(apiErrorText(l10n, error))),
      ),
    );
  }
}
