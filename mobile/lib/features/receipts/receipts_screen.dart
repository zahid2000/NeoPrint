import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/network/idempotency.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../../shared/widgets/neo_status_chip.dart';
import 'receipts_controller.dart';
import 'receipts_repository.dart';

/// The user's receipts, newest first (plan section 2.12).
class ReceiptsScreen extends ConsumerWidget {
  const ReceiptsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final receipts = ref.watch(receiptsProvider);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.receiptsTitle)),
      body: switch (receipts) {
        AsyncData(:final value) when value.isEmpty => NeoEmptyState(
          icon: Icons.receipt_long_outlined,
          message: l10n.receiptsEmpty,
          hint: l10n.receiptsEmptyHint,
        ),
        AsyncData(:final value) => RefreshIndicator(
          onRefresh: () => ref.refresh(receiptsProvider.future),
          child: ListView.separated(
            padding: const EdgeInsets.all(Spacing.x4),
            itemCount: value.length,
            separatorBuilder: (_, __) => const SizedBox(height: Spacing.x2),
            itemBuilder: (_, index) => _ReceiptCard(receipt: value[index]),
          ),
        ),
        AsyncError(:final error) => NeoEmptyState(
          icon: Icons.cloud_off_outlined,
          message: l10n.receiptsLoadFailed(
            error is ApiException
                ? apiErrorText(l10n, error)
                : l10n.errorServer,
          ),
          action: OutlinedButton(
            onPressed: () => ref.invalidate(receiptsProvider),
            child: Text(l10n.commonRetry),
          ),
        ),
        _ => const Center(child: CircularProgressIndicator()),
      },
    );
  }
}

class _ReceiptCard extends ConsumerStatefulWidget {
  const _ReceiptCard({required this.receipt});

  final Receipt receipt;

  @override
  ConsumerState<_ReceiptCard> createState() => _ReceiptCardState();
}

class _ReceiptCardState extends ConsumerState<_ReceiptCard> {
  bool _sending = false;

  Future<void> _email() async {
    final l10n = AppLocalizations.of(context);
    setState(() => _sending = true);

    final error = await emailReceipt(
      ref,
      widget.receipt.id,
      idempotencyKey: newIdempotencyKey(),
    );

    if (!mounted) {
      return;
    }

    setState(() => _sending = false);
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(
          error == null ? l10n.receiptEmailed : apiErrorText(l10n, error),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final receipt = widget.receipt;
    final issued = receipt.fiscalStatus == 'Issued';

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    receipt.number,
                    style: theme.textTheme.titleMedium?.copyWith(
                      fontFeatures: const [FontFeature.tabularFigures()],
                    ),
                  ),
                ),
                Text(
                  formatMoney(
                    receipt.amount,
                    language: Localizations.localeOf(context).languageCode,
                  ),
                  style: theme.textTheme.titleMedium?.copyWith(
                    fontFeatures: const [FontFeature.tabularFigures()],
                  ),
                ),
              ],
            ),
            const SizedBox(height: Spacing.x1),
            Text(
              [
                receipt.purpose == 'WalletTopUp'
                    ? l10n.receiptTopUp
                    : l10n.receiptPrint,
                formatDateTime(
                  receipt.createdAt,
                  today: l10n.commonToday,
                  yesterday: l10n.commonYesterday,
                ),
              ].join(' · '),
              style: theme.textTheme.bodySmall?.copyWith(
                color: tokens.neutral600,
              ),
            ),
            const SizedBox(height: Spacing.x2),
            Row(
              children: [
                // "None": no fiscal receipt belongs to this payment - no chip.
                // A failed try is still on its way as far as the customer is
                // concerned: an admin retries it.
                if (receipt.fiscalStatus != 'None')
                  NeoStatusChip(
                    label: issued
                        ? l10n.receiptFiscalIssued
                        : l10n.receiptFiscalPending,
                    tone: issued ? StatusTone.success : StatusTone.warning,
                  ),
                const Spacer(),
                TextButton.icon(
                  onPressed: _sending ? null : _email,
                  icon: const Icon(Icons.email_outlined),
                  label: Text(l10n.receiptEmail),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
