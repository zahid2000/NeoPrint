import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/network/idempotency.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../../shared/widgets/neo_primary_button.dart';
import 'wallet_controller.dart';
import 'wallet_repository.dart';

/// The balance, the rule that it never goes back to a card, adding money,
/// a voucher, and every movement (plan section 2.14; docs/DESIGN_SYSTEM.md
/// section 4, "Balans ekranı").
class WalletScreen extends ConsumerWidget {
  const WalletScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final wallet = ref.watch(walletProvider);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.walletTitle)),
      body: switch (wallet) {
        AsyncData(:final value) => RefreshIndicator(
          onRefresh: () => ref.read(walletProvider.notifier).refresh(),
          child: _WalletBody(state: value),
        ),
        AsyncError(:final error) => NeoEmptyState(
          icon: Icons.cloud_off_outlined,
          message: l10n.walletLoadFailed(
            error is ApiException
                ? apiErrorText(l10n, error)
                : l10n.errorServer,
          ),
          action: OutlinedButton(
            onPressed: () => ref.invalidate(walletProvider),
            child: Text(l10n.commonRetry),
          ),
        ),
        _ => const Center(child: CircularProgressIndicator()),
      },
    );
  }
}

class _WalletBody extends StatelessWidget {
  const _WalletBody({required this.state});

  final WalletState state;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final language = Localizations.localeOf(context).languageCode;

    return ListView(
      padding: const EdgeInsets.all(Spacing.x4),
      children: [
        Card(
          color: theme.colorScheme.primaryContainer,
          child: Padding(
            padding: const EdgeInsets.all(Spacing.x4),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  formatMoney(state.wallet.balance, language: language),
                  style: theme.textTheme.displaySmall?.copyWith(
                    color: theme.colorScheme.onPrimaryContainer,
                    fontWeight: FontWeights.semibold,
                    fontFeatures: const [FontFeature.tabularFigures()],
                  ),
                ),
                if (state.wallet.bonusPages > 0)
                  Text(
                    l10n.homeBonusPagesCount(state.wallet.bonusPages),
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: theme.colorScheme.onPrimaryContainer,
                    ),
                  ),
              ],
            ),
          ),
        ),
        const SizedBox(height: Spacing.x3),
        // Always on screen, not only at top-up: the rule the user agreed to
        // at registration (plan section 2.14).
        NeoBanner(message: l10n.walletNoRefundNote, tone: StatusTone.warning),
        const SizedBox(height: Spacing.x4),
        NeoPrimaryButton(
          label: l10n.walletTopUp,
          icon: Icons.add,
          onPressed: () => showTopUpSheet(context),
        ),
        const SizedBox(height: Spacing.x2),
        OutlinedButton.icon(
          onPressed: () => showVoucherDialog(context),
          icon: const Icon(Icons.confirmation_number_outlined),
          label: Text(l10n.walletVoucher),
        ),
        const SizedBox(height: Spacing.x6),
        Text(l10n.walletHistory, style: theme.textTheme.titleMedium),
        const SizedBox(height: Spacing.x2),
        if (state.entries.isEmpty)
          NeoEmptyState(
            icon: Icons.receipt_long_outlined,
            message: l10n.walletNoHistory,
            hint: l10n.walletNoHistoryHint,
          )
        else
          for (final entry in state.entries) _EntryTile(entry: entry),
      ],
    );
  }
}

class _EntryTile extends StatelessWidget {
  const _EntryTile({required this.entry});

  final WalletEntry entry;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final tokens = context.tokens;
    final theme = Theme.of(context);
    final language = Localizations.localeOf(context).languageCode;
    final incoming = entry.amount > 0;

    final (label, icon) = switch (entry.type) {
      'TopUp' => (l10n.walletTypeTopUp, Icons.add_card),
      'Payment' => (l10n.walletTypePayment, Icons.print_outlined),
      'Refund' => (l10n.walletTypeRefund, Icons.undo),
      'Bonus' => (l10n.walletTypeBonus, Icons.card_giftcard),
      'VoucherRedeem' => (
        l10n.walletTypeVoucherRedeem,
        Icons.confirmation_number_outlined,
      ),
      _ => (l10n.walletTypeAdminAdjust, Icons.tune),
    };

    final details = [
      if (entry.note?.isNotEmpty ?? false) entry.note!,
      if (entry.bonusPages != 0) l10n.homeBonusPagesCount(entry.bonusPages),
      formatDateTime(
        entry.createdAt,
        today: l10n.commonToday,
        yesterday: l10n.commonYesterday,
      ),
    ];

    return ListTile(
      contentPadding: EdgeInsets.zero,
      leading: Icon(icon),
      title: Text(label),
      subtitle: Text(details.join('\n')),
      isThreeLine: details.length > 1,
      trailing: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        crossAxisAlignment: CrossAxisAlignment.end,
        children: [
          Text(
            '${incoming ? '+' : ''}${formatMoney(entry.amount, language: language)}',
            style: theme.textTheme.bodyLarge?.copyWith(
              color: incoming ? tokens.success : tokens.neutral800,
              fontWeight: FontWeights.medium,
              fontFeatures: const [FontFeature.tabularFigures()],
            ),
          ),
          Text(
            l10n.walletBalanceAfter(
              formatMoney(entry.balanceAfter, language: language),
            ),
            style: theme.textTheme.bodySmall,
          ),
        ],
      ),
    );
  }
}

/// Adding money from a card: a few ready amounts and one of the user's own.
Future<void> showTopUpSheet(BuildContext context) => showModalBottomSheet<void>(
  context: context,
  isScrollControlled: true,
  showDragHandle: true,
  builder: (_) => const _TopUpSheet(),
);

class _TopUpSheet extends ConsumerStatefulWidget {
  const _TopUpSheet();

  @override
  ConsumerState<_TopUpSheet> createState() => _TopUpSheetState();
}

class _TopUpSheetState extends ConsumerState<_TopUpSheet> {
  static const presets = [2.0, 5.0, 10.0, 20.0];
  static const min = 1.0;
  static const max = 500.0;

  final _other = TextEditingController();
  double? _preset = 5;
  String _key = newIdempotencyKey();
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _other.dispose();
    super.dispose();
  }

  double? get _amount {
    if (_preset != null) {
      return _preset;
    }

    final typed = double.tryParse(_other.text.trim().replaceAll(',', '.'));
    if (typed == null || typed < min || typed > max) {
      return null;
    }

    return (typed * 100).roundToDouble() / 100;
  }

  Future<void> _submit() async {
    final l10n = AppLocalizations.of(context);
    final amount = _amount;

    if (amount == null) {
      setState(() => _error = l10n.walletTopUpRange);
      return;
    }

    setState(() {
      _busy = true;
      _error = null;
    });

    final error = await ref
        .read(walletProvider.notifier)
        .topUp(amount, idempotencyKey: _key);

    if (!mounted) {
      return;
    }

    if (error == null) {
      final messenger = ScaffoldMessenger.of(context);
      Navigator.of(context).pop();
      messenger.showSnackBar(
        SnackBar(
          content: Text(
            l10n.walletTopUpDone(
              formatMoney(
                amount,
                language: Localizations.localeOf(context).languageCode,
              ),
            ),
          ),
        ),
      );
      return;
    }

    // A card that said no is a finished attempt; a lost answer is retried
    // with the same key so the card is not charged twice.
    if (error.kind == ApiErrorKind.server && error.statusCode != 503) {
      _key = newIdempotencyKey();
    }

    setState(() {
      _busy = false;
      _error = apiErrorText(l10n, error);
    });
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final language = Localizations.localeOf(context).languageCode;
    final amount = _amount;

    return Padding(
      padding: EdgeInsets.only(
        left: Spacing.x4,
        right: Spacing.x4,
        bottom: MediaQuery.viewInsetsOf(context).bottom + Spacing.x4,
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(l10n.walletTopUpTitle, style: theme.textTheme.titleLarge),
          const SizedBox(height: Spacing.x3),
          NeoBanner(message: l10n.walletNoRefundNote, tone: StatusTone.warning),
          const SizedBox(height: Spacing.x4),
          Wrap(
            spacing: Spacing.x2,
            runSpacing: Spacing.x2,
            children: [
              for (final preset in presets)
                ChoiceChip(
                  label: Text(formatMoney(preset, language: language)),
                  selected: _preset == preset,
                  onSelected: _busy
                      ? null
                      : (_) => setState(() {
                          _preset = preset;
                          _other.clear();
                          _error = null;
                        }),
                ),
            ],
          ),
          const SizedBox(height: Spacing.x3),
          TextField(
            controller: _other,
            enabled: !_busy,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            decoration: InputDecoration(
              labelText: l10n.walletTopUpOther,
              helperText: l10n.walletTopUpRange,
            ),
            onChanged: (_) => setState(() {
              _preset = null;
              _error = null;
            }),
          ),
          if (_error != null) ...[
            const SizedBox(height: Spacing.x3),
            NeoBanner.error(message: _error!),
          ],
          const SizedBox(height: Spacing.x4),
          NeoPrimaryButton(
            label: l10n.walletTopUpPay(
              formatMoney(amount ?? 0, language: language),
            ),
            onPressed: _submit,
            busy: _busy,
          ),
        ],
      ),
    );
  }
}

/// A refund voucher into the wallet.
Future<void> showVoucherDialog(BuildContext context) =>
    showDialog<void>(context: context, builder: (_) => const _VoucherDialog());

class _VoucherDialog extends ConsumerStatefulWidget {
  const _VoucherDialog();

  @override
  ConsumerState<_VoucherDialog> createState() => _VoucherDialogState();
}

class _VoucherDialogState extends ConsumerState<_VoucherDialog> {
  final _code = TextEditingController();
  String _key = newIdempotencyKey();
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _code.dispose();
    super.dispose();
  }

  Future<void> _redeem() async {
    final l10n = AppLocalizations.of(context);
    final code = _code.text.trim().toUpperCase();

    if (code.isEmpty) {
      setState(() => _error = l10n.walletVoucherEmpty);
      return;
    }

    setState(() {
      _busy = true;
      _error = null;
    });

    final error = await ref
        .read(walletProvider.notifier)
        .redeemVoucher(code, idempotencyKey: _key);

    if (!mounted) {
      return;
    }

    if (error == null) {
      final messenger = ScaffoldMessenger.of(context);
      Navigator.of(context).pop();
      messenger.showSnackBar(SnackBar(content: Text(l10n.walletVoucherDone)));
      return;
    }

    if (error.kind == ApiErrorKind.server) {
      _key = newIdempotencyKey();
    }

    setState(() {
      _busy = false;
      _error = apiErrorText(l10n, error);
    });
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    return AlertDialog(
      title: Text(l10n.walletVoucherTitle),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          TextField(
            controller: _code,
            enabled: !_busy,
            autofocus: true,
            textCapitalization: TextCapitalization.characters,
            decoration: InputDecoration(
              labelText: l10n.walletVoucher,
              helperText: l10n.walletVoucherHint,
              helperMaxLines: 2,
            ),
          ),
          if (_error != null) ...[
            const SizedBox(height: Spacing.x3),
            NeoBanner.error(message: _error!),
          ],
        ],
      ),
      actions: [
        TextButton(
          onPressed: _busy ? null : () => Navigator.of(context).pop(),
          child: Text(l10n.commonCancel),
        ),
        FilledButton(
          onPressed: _busy ? null : _redeem,
          child: Text(l10n.walletVoucherRedeem),
        ),
      ],
    );
  }
}
