import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_client.dart';
import '../../core/realtime/user_hub.dart';
import '../../core/realtime/user_hub_provider.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import 'schedule_actions.dart';
import 'schedule_models.dart';
import 'schedule_repository.dart';

/// When a scheduled print's time comes (`ScheduleDue` on the user hub), asks
/// "Çapa ver" or "Vaxtı dəyiş" wherever the user is in the app (plan section
/// 9.5, step 2; docs/DESIGN_SYSTEM.md "Mobil: planlanmış çap").
///
/// Several due at the same minute share one dialog, each with its own buttons
/// (T-159), instead of dialogs stacked on top of each other.
class ScheduleDuePrompt extends ConsumerStatefulWidget {
  const ScheduleDuePrompt({super.key, required this.child});

  final Widget child;

  @override
  ConsumerState<ScheduleDuePrompt> createState() => _ScheduleDuePromptState();
}

class _ScheduleDuePromptState extends ConsumerState<ScheduleDuePrompt> {
  StreamSubscription<HubEvent>? _subscription;

  /// What the open dialog asks about; null while none is open.
  ValueNotifier<List<ScheduledPrint>>? _due;

  /// Schedules being looked up: the same news twice asks once.
  final _looking = <String>{};

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
    if (id is! String ||
        _looking.contains(id) ||
        (_due?.value.any((s) => s.id == id) ?? false)) {
      return;
    }

    _looking.add(id);

    try {
      // The list is read again for this news; the dialog shows what it says.
      final items = await ref.read(scheduleRepositoryProvider).list();
      final item = items.where((s) => s.id == id).firstOrNull;

      if (item == null || !item.status.isActive || !mounted) {
        return;
      }

      if (_due case final open?) {
        // A dialog is already asking: this one joins its list.
        if (!open.value.any((s) => s.id == id)) {
          open.value = [...open.value, item];
        }
        return;
      }

      final due = _due = ValueNotifier([item]);

      try {
        await showDialog<void>(
          context: context,
          builder: (_) => ScheduleDueDialog(due: due),
        );
      } finally {
        _due = null;
      }
    } on Exception {
      // The list could not be read: the tab shows it once it can.
    } finally {
      _looking.remove(id);
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

/// The schedules whose time has come; answered ones leave the list, and the
/// dialog closes when it is empty.
class ScheduleDueDialog extends ConsumerStatefulWidget {
  const ScheduleDueDialog({super.key, required this.due});

  final ValueNotifier<List<ScheduledPrint>> due;

  @override
  ConsumerState<ScheduleDueDialog> createState() => _ScheduleDueDialogState();
}

class _ScheduleDueDialogState extends ConsumerState<ScheduleDueDialog> {
  final _busy = <String>{};
  final _errors = <String, String>{};

  void _answered(ScheduledPrint item) {
    _errors.remove(item.id);
    widget.due.value = [
      for (final s in widget.due.value)
        if (s.id != item.id) s,
    ];

    if (widget.due.value.isEmpty && mounted) {
      Navigator.of(context).pop();
    }
  }

  Future<void> _print(ScheduledPrint item) async {
    final l10n = AppLocalizations.of(context);
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _busy.add(item.id));

    try {
      await ref.read(schedulesProvider.notifier).printNow(item.id);
      messenger.showSnackBar(
        SnackBar(
          content: Text(l10n.schedSentDone(item.kioskCode ?? '', item.jobCode)),
        ),
      );
      _answered(item);
    } on ApiException catch (error) {
      // The kiosk cannot take it now: why, and "Vaxtı dəyiş" stays right here.
      _errors[item.id] = l10n.schedPrintFailedBody(apiErrorText(l10n, error));
    } finally {
      if (mounted) {
        setState(() => _busy.remove(item.id));
      }
    }
  }

  Future<void> _move(ScheduledPrint item) async {
    final l10n = AppLocalizations.of(context);
    final messenger = ScaffoldMessenger.of(context);
    final at = await pickScheduleTime(context, initial: item.scheduledAt);

    if (at == null || !mounted) {
      return;
    }

    final when = scheduleTime(context, at);
    setState(() => _busy.add(item.id));

    try {
      await ref.read(schedulesProvider.notifier).reschedule(item.id, at);
      messenger.showSnackBar(
        SnackBar(content: Text(l10n.schedMovedDone(when))),
      );
      _answered(item);
    } on ApiException catch (error) {
      _errors[item.id] = l10n.schedActionFailed(apiErrorText(l10n, error));
    } finally {
      if (mounted) {
        setState(() => _busy.remove(item.id));
      }
    }
  }

  List<Widget> _buttons(ScheduledPrint item) {
    final l10n = AppLocalizations.of(context);
    final busy = _busy.contains(item.id);

    return [
      TextButton(
        onPressed: busy ? null : () => _move(item),
        child: Text(l10n.schedChangeTime),
      ),
      FilledButton(
        onPressed: busy ? null : () => _print(item),
        child: Text(l10n.schedPrintNow),
      ),
    ];
  }

  Widget? _error(ScheduledPrint item) => switch (_errors[item.id]) {
    final String text => Padding(
      padding: const EdgeInsets.only(top: Spacing.x2),
      child: NeoBanner.error(message: text),
    ),
    _ => null,
  };

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final text = Theme.of(context).textTheme;
    final icon = Icon(
      Icons.notifications_active_outlined,
      color: context.tokens.brand500,
    );

    return ValueListenableBuilder<List<ScheduledPrint>>(
      valueListenable: widget.due,
      builder: (context, items, _) {
        if (items.isEmpty) {
          return const SizedBox.shrink();
        }

        // One: the dialog as it always was, the error under its sentence.
        if (items.length == 1) {
          final item = items.single;
          return AlertDialog(
            icon: icon,
            title: Text(l10n.schedDueTitle),
            content: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(l10n.schedDueBody(item.fileName, item.kioskCode ?? '')),
                ?_error(item),
              ],
            ),
            actions: _buttons(item),
          );
        }

        return AlertDialog(
          icon: icon,
          title: Text(l10n.schedDueManyTitle),
          content: SizedBox(
            width: double.maxFinite,
            child: ListView.separated(
              shrinkWrap: true,
              itemCount: items.length,
              separatorBuilder: (_, __) => const Divider(height: Spacing.x6),
              itemBuilder: (context, index) {
                final item = items[index];
                return Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Text(item.fileName, style: text.titleSmall),
                    const SizedBox(height: Spacing.x1),
                    Text(
                      l10n.schedDueRow(
                        item.kioskCode ?? '',
                        scheduleTime(context, item.scheduledAt),
                      ),
                      style: text.bodySmall?.copyWith(
                        color: context.tokens.neutral600,
                      ),
                    ),
                    ?_error(item),
                    const SizedBox(height: Spacing.x2),
                    Wrap(
                      alignment: WrapAlignment.end,
                      spacing: Spacing.x2,
                      children: _buttons(item),
                    ),
                  ],
                );
              },
            ),
          ),
        );
      },
    );
  }
}
