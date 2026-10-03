import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../../shared/widgets/neo_primary_button.dart';
import '../../shared/widgets/neo_status_chip.dart';
import 'prepared_models.dart';
import 'prepared_repository.dart';

/// "Hazırladıqlarım" (plan section 9.4; docs/DESIGN_SYSTEM.md "Mobil: şəxsi
/// QR və Kiosk üçün hazırla"): what is ready for a kiosk, and the QR that
/// opens it there.
class PreparedListScreen extends ConsumerWidget {
  const PreparedListScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final items = ref.watch(preparedPrintsProvider);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.preparedTitle)),
      body: RefreshIndicator(
        onRefresh: () => ref.read(preparedPrintsProvider.notifier).refresh(),
        child: switch (items) {
          AsyncData(:final value) when value.isEmpty => ListView(
            children: [
              NeoEmptyState(
                icon: Icons.inventory_2_outlined,
                message: l10n.preparedEmpty,
                hint: l10n.preparedEmptyHint,
              ),
            ],
          ),
          AsyncData(:final value) => ListView.separated(
            padding: const EdgeInsets.all(Spacing.x4),
            itemCount: value.length,
            separatorBuilder: (_, __) => const SizedBox(height: Spacing.x3),
            itemBuilder: (_, index) => _PreparedCard(item: value[index]),
          ),
          AsyncError(:final error) => ListView(
            children: [
              NeoEmptyState(
                icon: Icons.cloud_off_outlined,
                message: l10n.preparedLoadFailed(
                  error is ApiException
                      ? apiErrorText(l10n, error)
                      : l10n.errorServer,
                ),
                action: OutlinedButton(
                  onPressed: () => ref.invalidate(preparedPrintsProvider),
                  child: Text(l10n.commonRetry),
                ),
              ),
            ],
          ),
          _ => const Center(child: CircularProgressIndicator()),
        },
      ),
      bottomNavigationBar: SafeArea(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(
            Spacing.x4,
            Spacing.x2,
            Spacing.x4,
            Spacing.x4,
          ),
          child: NeoPrimaryButton(
            label: l10n.myQrTitle,
            icon: Icons.qr_code_2,
            onPressed: () => context.push(Routes.myQr),
          ),
        ),
      ),
    );
  }
}

class _PreparedCard extends ConsumerWidget {
  const _PreparedCard({required this.item});

  final PreparedPrint item;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final language = Localizations.localeOf(context).languageCode;

    final (label, tone, icon) = switch (item.state) {
      PreparedState.ready => (
        l10n.preparedStateReady,
        StatusTone.info,
        Icons.schedule,
      ),
      PreparedState.printing => (
        l10n.preparedStatePrinting,
        StatusTone.info,
        Icons.print_outlined,
      ),
      PreparedState.printed => (
        l10n.preparedStatePrinted,
        StatusTone.success,
        Icons.check_circle_outline,
      ),
      PreparedState.fileUnavailable => (
        l10n.preparedStateFileUnavailable,
        StatusTone.warning,
        Icons.warning_amber_outlined,
      ),
      PreparedState.paid => (
        l10n.preparedStatePaid,
        StatusTone.success,
        Icons.check_circle_outline,
      ),
    };

    return Card(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Icon(Icons.description_outlined, color: tokens.brand500),
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
                    _detail(l10n),
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: tokens.neutral600,
                    ),
                  ),
                  const SizedBox(height: Spacing.x2),
                  NeoStatusChip(label: label, tone: tone, icon: icon),
                  if (item.isPaid) ...[
                    const SizedBox(height: Spacing.x2),
                    Text(
                      l10n.preparedPaidAt(
                        item.kioskCode ?? '',
                        formatMoney(item.amount ?? 0, language: language),
                      ),
                      style: theme.textTheme.bodyMedium,
                    ),
                    if (item.collectBy case final by?)
                      Text(
                        l10n.preparedCollectBy(
                          formatDateTime(
                            by,
                            today: l10n.commonToday,
                            yesterday: l10n.commonYesterday,
                          ),
                        ),
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: tokens.neutral600,
                        ),
                      ),
                  ],
                  if (item.state == PreparedState.fileUnavailable) ...[
                    const SizedBox(height: Spacing.x1),
                    Text(
                      l10n.preparedFileUnavailableHint,
                      style: theme.textTheme.bodySmall,
                    ),
                  ],
                ],
              ),
            ),
            PopupMenuButton<String>(
              tooltip: l10n.commonMore,
              onSelected: (_) => unawaited(
                item.isPaid ? _cancel(context, ref) : _remove(context, ref),
              ),
              itemBuilder: (_) => [
                PopupMenuItem(
                  value: 'act',
                  child: ListTile(
                    leading: Icon(
                      item.isPaid
                          ? Icons.cancel_outlined
                          : Icons.delete_outline,
                      color: tokens.danger,
                    ),
                    title: Text(
                      item.isPaid ? l10n.preparedCancel : l10n.preparedRemove,
                    ),
                    contentPadding: EdgeInsets.zero,
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  /// Pages and what was chosen in the app; nothing chosen says so.
  String _detail(AppLocalizations l10n) {
    final parts = [l10n.filesPageCount(item.pages)];
    final options = item.options;
    final preset = item.preset;

    if (options != null) {
      if (options.copies > 1) parts.add('${options.copies} ×');
      parts.add(options.color ? l10n.optColorful : l10n.optBlackWhite);
      parts.add(options.paper);
    } else if (preset != null && !preset.isEmpty) {
      if (preset.copies case final copies? when copies > 1) {
        parts.add('$copies ×');
      }
      if (preset.color case final color?) {
        parts.add(color ? l10n.optColorful : l10n.optBlackWhite);
      }
      if (preset.paper case final paper?) parts.add(paper);
    } else {
      parts.add(l10n.preparedAtKiosk);
    }

    return parts.join(' · ');
  }

  Future<void> _remove(BuildContext context, WidgetRef ref) async {
    await _act(
      context,
      () => ref.read(preparedPrintsProvider.notifier).remove(item.id),
    );
  }

  Future<void> _cancel(BuildContext context, WidgetRef ref) async {
    final l10n = AppLocalizations.of(context);
    final language = Localizations.localeOf(context).languageCode;

    final sure = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(l10n.preparedCancelTitle),
        content: Text(
          l10n.preparedCancelBody(
            formatMoney(item.amount ?? 0, language: language),
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: Text(l10n.commonClose),
          ),
          FilledButton(
            style: FilledButton.styleFrom(
              backgroundColor: context.tokens.danger,
            ),
            onPressed: () => Navigator.pop(context, true),
            child: Text(l10n.preparedCancel),
          ),
        ],
      ),
    );

    if (sure == true && context.mounted) {
      await _act(
        context,
        () => ref.read(preparedPrintsProvider.notifier).cancelPaid(item.id),
      );
    }
  }

  Future<void> _act(
    BuildContext context,
    Future<void> Function() action,
  ) async {
    final messenger = ScaffoldMessenger.of(context);
    final l10n = AppLocalizations.of(context);

    try {
      await action();
    } on ApiException catch (error) {
      messenger.showSnackBar(
        SnackBar(
          content: Text(l10n.preparedActionFailed(apiErrorText(l10n, error))),
        ),
      );
    }
  }
}
