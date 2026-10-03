import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/realtime/user_hub.dart';
import '../../core/realtime/user_hub_provider.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import 'schedule_actions.dart';
import 'schedule_repository.dart';

/// When a scheduled print's time comes (`ScheduleDue` on the user hub), asks
/// "Çapa ver" or "Vaxtı dəyiş" wherever the user is in the app (plan section
/// 9.5, step 2; docs/DESIGN_SYSTEM.md "Mobil: planlanmış çap").
class ScheduleDuePrompt extends ConsumerStatefulWidget {
  const ScheduleDuePrompt({super.key, required this.child});

  final Widget child;

  @override
  ConsumerState<ScheduleDuePrompt> createState() => _ScheduleDuePromptState();
}

class _ScheduleDuePromptState extends ConsumerState<ScheduleDuePrompt> {
  StreamSubscription<HubEvent>? _subscription;

  /// The schedule asked about right now: the same news twice asks once.
  String? _asking;

  @override
  void initState() {
    super.initState();
    ref.listenManual<Stream<HubEvent>>(
      userHubEventsProvider,
      (_, events) => _listen(events),
      fireImmediately: true,
    );
  }

  void _listen(Stream<HubEvent> events) {
    unawaited(_subscription?.cancel());
    _subscription = events
        .where((event) => event.target == 'ScheduleDue')
        .listen((event) => unawaited(_ask(event.payload['scheduleId'])));
  }

  Future<void> _ask(Object? id) async {
    if (id is! String || id == _asking) {
      return;
    }

    _asking = id;

    try {
      // The list is read again for this news; the dialog shows what it says.
      final items = await ref.read(scheduleRepositoryProvider).list();
      final item = items.where((s) => s.id == id).firstOrNull;

      if (item == null || !item.status.isActive || !mounted) {
        return;
      }

      final l10n = AppLocalizations.of(context);
      final choice = await showDialog<String>(
        context: context,
        builder: (context) => AlertDialog(
          icon: Icon(
            Icons.notifications_active_outlined,
            color: context.tokens.brand500,
          ),
          title: Text(l10n.schedDueTitle),
          content: Text(l10n.schedDueBody(item.fileName, item.kioskCode ?? '')),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(context, 'time'),
              child: Text(l10n.schedChangeTime),
            ),
            FilledButton(
              onPressed: () => Navigator.pop(context, 'print'),
              child: Text(l10n.schedPrintNow),
            ),
          ],
        ),
      );

      if (!mounted) {
        return;
      }

      switch (choice) {
        case 'print':
          await printScheduleNow(context, ref, item);
        case 'time':
          await rescheduleSchedule(context, ref, item);
      }
    } on Exception {
      // The list could not be read: the tab shows it once it can.
    } finally {
      _asking = null;
    }
  }

  @override
  void dispose() {
    unawaited(_subscription?.cancel());
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
