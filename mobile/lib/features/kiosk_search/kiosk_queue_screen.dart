import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_design.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../../shared/widgets/neo_status_chip.dart';
import '../../core/network/api_client.dart';
import 'kiosk_search_controller.dart';

/// One order in a kiosk's queue (`GET api/kiosks/{id}/queue`, T-379):
/// whose it is never, but the user's own is marked with its code.
final class QueueRow {
  const QueueRow({
    required this.position,
    required this.sides,
    required this.printedSides,
    required this.printing,
    required this.isMine,
    this.jobCode,
    required this.readyInMinutes,
  });

  factory QueueRow.fromJson(Map<String, dynamic> json) => QueueRow(
    position: json['position'] as int,
    sides: json['sides'] as int? ?? 0,
    printedSides: json['printedSides'] as int? ?? 0,
    printing: json['status'] == 'Printing',
    isMine: json['isMine'] as bool? ?? false,
    jobCode: json['jobCode'] as String?,
    readyInMinutes: json['readyInMinutes'] as int? ?? 1,
  );

  final int position;
  final int sides;
  final int printedSides;
  final bool printing;
  final bool isMine;
  final String? jobCode;

  /// A rough estimate: "≈ N dəq".
  final int readyInMinutes;
}

/// A kiosk's queue, read again now and then while it is on screen.
final kioskQueueProvider = FutureProvider.autoDispose
    .family<List<QueueRow>, String>((ref, kioskId) async {
      final timer = Timer(kioskQueueRefresh, ref.invalidateSelf);
      ref.onDispose(timer.cancel);
      return ref.watch(kioskRepositoryProvider).queue(kioskId);
    });

/// How often the queue is read again while it is open.
const kioskQueueRefresh = Duration(seconds: 15);

/// "Çap növbəsi" (T-379; the poster's "Çap növbəsi"): the kiosk's queue
/// row by row - the one printing first, the user's own order marked.
class KioskQueueScreen extends ConsumerWidget {
  const KioskQueueScreen({
    super.key,
    required this.kioskId,
    required this.kioskName,
  });

  final String kioskId;
  final String kioskName;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final queue = ref.watch(kioskQueueProvider(kioskId));

    return Scaffold(
      appBar: neoTabAppBar(context, title: l10n.queueTitle, showBack: true),
      body: RefreshIndicator(
        onRefresh: () => ref.refresh(kioskQueueProvider(kioskId).future),
        child: switch (queue) {
          AsyncData(:final value) when value.isEmpty => ListView(
            children: [
              const SizedBox(height: Spacing.x8),
              NeoEmptyState(
                icon: Icons.inbox_outlined,
                message: l10n.kioskQueueEmpty,
                hint: l10n.queueEmptyHint(kioskName),
              ),
            ],
          ),
          AsyncData(:final value) => ListView(
            padding: const EdgeInsets.fromLTRB(
              Spacing.x4,
              Spacing.x2,
              Spacing.x4,
              Spacing.x8,
            ),
            children: [
              Text(
                l10n.queueSubtitle(kioskName),
                style: Theme.of(context).textTheme.bodyMedium
                    ?.copyWith(color: context.tokens.neutral600),
              ),
              const SizedBox(height: Spacing.x3),
              for (final row in value) ...[
                _Row(row: row),
                const SizedBox(height: Spacing.x2),
              ],
            ],
          ),
          AsyncError(:final error) => ListView(
            padding: const EdgeInsets.all(Spacing.x4),
            children: [
              NeoBanner.error(
                message: error is ApiException
                    ? apiErrorText(l10n, error)
                    : l10n.queueLoadFailed,
              ),
            ],
          ),
          _ => const Center(child: CircularProgressIndicator()),
        },
      ),
    );
  }
}

class _Row extends StatelessWidget {
  const _Row({required this.row});

  final QueueRow row;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;

    return Card(
      margin: EdgeInsets.zero,
      color: row.isMine ? tokens.brand50 : null,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(Radii.md),
        side: BorderSide(
          color: row.isMine ? tokens.brand500 : tokens.neutral200,
          width: row.isMine ? 2 : 1,
        ),
      ),
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x3),
        child: Row(
          children: [
            SizedBox(
              width: Spacing.x12,
              child: Text(
                '#${row.position}',
                style: theme.textTheme.titleMedium?.copyWith(
                  fontWeight: FontWeights.semibold,
                  color: row.isMine ? tokens.brand500 : tokens.neutral600,
                ),
              ),
            ),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    row.isMine
                        ? l10n.queueMine(row.jobCode ?? '')
                        : l10n.queueSomeone,
                    style: theme.textTheme.titleSmall?.copyWith(
                      fontWeight: row.isMine ? FontWeights.semibold : null,
                    ),
                  ),
                  const SizedBox(height: Spacing.x1),
                  Text(
                    row.printing
                        ? l10n.queuePrintedOf(row.printedSides, row.sides)
                        : l10n.filesPageCount(row.sides),
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: tokens.neutral600,
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(width: Spacing.x2),
            Column(
              crossAxisAlignment: CrossAxisAlignment.end,
              children: [
                NeoStatusChip(
                  label: row.printing
                      ? l10n.jobStatusPrinting
                      : l10n.jobStatusQueued,
                  tone: row.printing ? StatusTone.success : StatusTone.warning,
                  icon: row.printing ? Icons.print_outlined : Icons.schedule,
                ),
                const SizedBox(height: Spacing.x1),
                Text(
                  l10n.queueReadyIn(row.readyInMinutes),
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: tokens.neutral600,
                    fontFeatures: const [FontFeature.tabularFigures()],
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
