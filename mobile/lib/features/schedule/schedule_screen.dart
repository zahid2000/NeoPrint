import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/features/feature_flags.dart';
import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_design.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../files/file_models.dart';
import '../../shared/widgets/neo_status_chip.dart';
import 'schedule_actions.dart';
import 'schedule_models.dart';
import 'schedule_repository.dart';

/// "Planlılar · Keçmiş" (T-342; the design's screen 13).
enum _ScheduleTab { upcoming, past }

/// "Planlı çap" (plan sections 8.2 and 9.5; docs/DESIGN_SYSTEM.md "Mobil:
/// planlanmış çap"): the ones still to print - unpaid, waiting, due - and
/// the ones done, a tab each; a new one from the round "+".
class ScheduleScreen extends ConsumerStatefulWidget {
  const ScheduleScreen({super.key});

  @override
  ConsumerState<ScheduleScreen> createState() => _ScheduleScreenState();
}

class _ScheduleScreenState extends ConsumerState<ScheduleScreen> {
  _ScheduleTab _tab = _ScheduleTab.upcoming;

  static bool _upcoming(ScheduledPrint item) =>
      item.status.isActive || item.status == ScheduleStatus.pendingPayment;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final items = ref.watch(schedulesProvider);
    void create() => unawaited(context.push(Routes.scheduleNew));
    // Switched off: what was paid stays here, nothing new is offered (T-148).
    final canCreate = ref.watch(featureOnProvider(FeatureKey.scheduledPrint));

    return Scaffold(
      appBar: neoTabAppBar(
        context,
        title: l10n.homeScheduled,
        showBack: Navigator.of(context).canPop(),
      ),
      floatingActionButton: switch (items) {
        AsyncData(:final value) when value.isNotEmpty && canCreate =>
          FloatingActionButton(
            tooltip: l10n.schedNew,
            shape: const CircleBorder(),
            onPressed: create,
            child: const Icon(Icons.add),
          ),
        _ => null,
      },
      body: RefreshIndicator(
        onRefresh: () => ref.read(schedulesProvider.notifier).refresh(),
        child: switch (items) {
          AsyncData(:final value) when value.isEmpty => ListView(
            children: [
              NeoEmptyState(
                icon: Icons.event_outlined,
                message: l10n.schedEmpty,
                hint: canCreate ? l10n.schedEmptyHint : null,
                action: canCreate
                    ? FilledButton.icon(
                        onPressed: create,
                        icon: const Icon(Icons.add),
                        label: Text(l10n.schedNew),
                      )
                    : null,
              ),
            ],
          ),
          AsyncData(:final value) => () {
            final shown = [
              for (final item in value)
                if (_upcoming(item) == (_tab == _ScheduleTab.upcoming)) item,
            ];
            return ListView(
              padding: const EdgeInsets.fromLTRB(
                Spacing.x4,
                Spacing.x2,
                Spacing.x4,
                // Room for the round button over the last card.
                Spacing.x16 + Spacing.x8,
              ),
              children: [
                NeoPillTabs<_ScheduleTab>(
                  values: _ScheduleTab.values,
                  selected: _tab,
                  expand: true,
                  label: (tab) => switch (tab) {
                    _ScheduleTab.upcoming => l10n.schedTabUpcoming,
                    _ScheduleTab.past => l10n.schedTabPast,
                  },
                  onSelected: (tab) => setState(() => _tab = tab),
                ),
                const SizedBox(height: Spacing.x3),
                if (shown.isEmpty)
                  NeoEmptyState(
                    icon: Icons.event_outlined,
                    message: _tab == _ScheduleTab.upcoming
                        ? l10n.schedEmpty
                        : l10n.schedPastEmpty,
                  )
                else
                  for (final item in shown) ...[
                    _ScheduleCard(item: item),
                    const SizedBox(height: Spacing.x3),
                  ],
              ],
            );
          }(),
          AsyncError(:final error) => ListView(
            children: [
              NeoEmptyState(
                icon: Icons.cloud_off_outlined,
                message: l10n.schedLoadFailed(
                  error is ApiException
                      ? apiErrorText(l10n, error)
                      : l10n.errorServer,
                ),
                action: OutlinedButton(
                  onPressed: () => ref.invalidate(schedulesProvider),
                  child: Text(l10n.commonRetry),
                ),
              ),
            ],
          ),
          _ => const Center(child: CircularProgressIndicator()),
        },
      ),
    );
  }
}

class _ScheduleCard extends ConsumerWidget {
  const _ScheduleCard({required this.item});

  final ScheduledPrint item;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final language = Localizations.localeOf(context).languageCode;

    final (label, tone, icon) = switch (item.status) {
      ScheduleStatus.pendingPayment => (
        l10n.schedStatusPendingPayment,
        StatusTone.warning,
        Icons.hourglass_empty,
      ),
      ScheduleStatus.scheduled => (
        l10n.schedStatusScheduled,
        StatusTone.info,
        Icons.event_available_outlined,
      ),
      ScheduleStatus.awaitingUser => (
        l10n.schedStatusAwaitingUser,
        StatusTone.warning,
        Icons.notifications_active_outlined,
      ),
      ScheduleStatus.sent when item.jobStatus == 'Completed' => (
        l10n.schedStatusPrinted,
        StatusTone.success,
        Icons.check_circle_outline,
      ),
      ScheduleStatus.sent => (
        l10n.schedStatusSent,
        StatusTone.success,
        Icons.print_outlined,
      ),
      ScheduleStatus.cancelled => (
        l10n.schedStatusCancelled,
        StatusTone.neutral,
        Icons.cancel_outlined,
      ),
      ScheduleStatus.expired => (
        l10n.schedStatusExpired,
        StatusTone.neutral,
        Icons.history_toggle_off,
      ),
    };

    final copies = item.options.copies;
    final detail = [
      copies > 1
          ? l10n.jobPagesCopies(item.pages, copies)
          : l10n.filesPageCount(item.pages),
      item.options.color ? l10n.optColorful : l10n.optBlackWhite,
      item.options.paper,
      formatMoney(item.amount, language: language),
    ].join(' · ');

    return Card(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                FileTypeTile(extension: extensionOf(item.fileName)),
                const SizedBox(width: Spacing.x3),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        item.fileName,
                        style: theme.textTheme.titleMedium,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                      ),
                      const SizedBox(height: Spacing.x1),
                      Text(
                        l10n.schedWhereWhen(
                          item.kioskCode ?? '',
                          scheduleTime(context, item.scheduledAt),
                        ),
                        style: theme.textTheme.bodyMedium,
                      ),
                      Text(
                        detail,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: tokens.neutral600,
                        ),
                      ),
                      const SizedBox(height: Spacing.x2),
                      NeoStatusChip(label: label, tone: tone, icon: icon),
                      if (item.status == ScheduleStatus.expired) ...[
                        const SizedBox(height: Spacing.x1),
                        Text(
                          l10n.schedExpiredRefunded,
                          style: theme.textTheme.bodySmall,
                        ),
                      ],
                    ],
                  ),
                ),
                if (item.status.isActive ||
                    item.status == ScheduleStatus.pendingPayment)
                  _Menu(item: item),
              ],
            ),
            if (item.status == ScheduleStatus.awaitingUser) ...[
              const SizedBox(height: Spacing.x3),
              if (item.autoCancelAt case final by?)
                Text(
                  l10n.schedAutoCancelHint(scheduleTime(context, by)),
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: tokens.neutral600,
                  ),
                ),
              const SizedBox(height: Spacing.x2),
              Row(
                children: [
                  Expanded(
                    child: FilledButton.icon(
                      onPressed: () =>
                          unawaited(printScheduleNow(context, ref, item)),
                      icon: const Icon(Icons.print_outlined),
                      label: Text(l10n.schedPrintNow),
                    ),
                  ),
                  const SizedBox(width: Spacing.x2),
                  TextButton(
                    onPressed: () =>
                        unawaited(rescheduleSchedule(context, ref, item)),
                    child: Text(l10n.schedChangeTime),
                  ),
                ],
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _Menu extends ConsumerWidget {
  const _Menu({required this.item});

  final ScheduledPrint item;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final tokens = context.tokens;
    final unpaid = item.status == ScheduleStatus.pendingPayment;

    PopupMenuItem<String> entry(
      String value,
      IconData icon,
      String text, {
      Color? colour,
    }) => PopupMenuItem(
      value: value,
      child: ListTile(
        leading: Icon(icon, color: colour ?? tokens.brand500),
        title: Text(text),
        contentPadding: EdgeInsets.zero,
      ),
    );

    return PopupMenuButton<String>(
      tooltip: l10n.commonMore,
      onSelected: (choice) => unawaited(switch (choice) {
        'pay' => context.push(Routes.pay(item.paymentId ?? '')),
        'print' => printScheduleNow(context, ref, item),
        'time' => rescheduleSchedule(context, ref, item),
        _ => cancelSchedule(context, ref, item),
      }),
      itemBuilder: (_) => [
        if (unpaid && item.paymentId != null)
          entry('pay', Icons.payments_outlined, l10n.schedPay),
        if (!unpaid) ...[
          entry('print', Icons.print_outlined, l10n.schedPrintNow),
          entry('time', Icons.edit_calendar_outlined, l10n.schedChangeTime),
        ],
        entry(
          'cancel',
          Icons.cancel_outlined,
          l10n.schedCancel,
          colour: tokens.danger,
        ),
      ],
    );
  }
}
