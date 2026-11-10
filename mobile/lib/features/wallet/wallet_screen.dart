import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/network/idempotency.dart';

import 'package:go_router/go_router.dart';

import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_design.dart';
import '../../shared/widgets/neo_icon_tile.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../../shared/widgets/neo_primary_button.dart';
import 'wallet_controller.dart';
import 'wallet_repository.dart';

/// The balance, the rule that it never goes back to a card, adding money,
/// a voucher, and every movement (plan section 2.14; docs/DESIGN_SYSTEM.md
/// section 4, "Balans ekranı").
class WalletScreen extends ConsumerStatefulWidget {
  const WalletScreen({super.key, this.voucher});

  /// From a voucher link (neoprint://voucher/{code}, plan section 8.3): the
  /// voucher dialog opens with it filled in.
  final String? voucher;

  @override
  ConsumerState<WalletScreen> createState() => _WalletScreenState();
}

class _WalletScreenState extends ConsumerState<WalletScreen> {
  @override
  void initState() {
    super.initState();
    if (widget.voucher case final code?) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) {
          showVoucherDialog(context, code: code);
        }
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final wallet = ref.watch(walletProvider);

    return Scaffold(
      appBar: neoTabAppBar(context, title: l10n.walletTitle),
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

enum _WalletTab { history, vouchers }

/// How many movements the balance screen shows; the rest are behind
/// "Hamısına bax" (T-380).
const walletRecentCount = 5;

/// T-355, as the design draws "Balansım": one blue card with the balance,
/// "Artır" and the bonus pages; the no-refund rule as one line under it;
/// then "Əməliyyatlar" and the voucher codes as two tabs.
class _WalletBody extends StatefulWidget {
  const _WalletBody({required this.state});

  final WalletState state;

  @override
  State<_WalletBody> createState() => _WalletBodyState();
}

class _WalletBodyState extends State<_WalletBody> {
  _WalletTab _tab = _WalletTab.history;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final state = widget.state;

    return NeoTabSwipe<_WalletTab>(
      values: _WalletTab.values,
      selected: _tab,
      onSelected: (tab) => setState(() => _tab = tab),
      child: ListView(
        padding: const EdgeInsets.all(Spacing.x4),
        children: [
          _BalanceCard(wallet: state.wallet),
          const SizedBox(height: Spacing.x3),
          // Always on screen, not only at top-up: the rule the user agreed to
          // at registration (plan section 2.14) - one quiet line, not a card.
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Icon(Icons.info_outline, color: tokens.warning),
              const SizedBox(width: Spacing.x2),
              Expanded(
                child: Text(
                  l10n.walletNoRefundNote,
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: tokens.neutral600,
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: Spacing.x4),
          NeoPillTabs<_WalletTab>(
            values: _WalletTab.values,
            selected: _tab,
            expand: true,
            label: (tab) => switch (tab) {
              _WalletTab.history => l10n.walletTabHistory,
              _WalletTab.vouchers => l10n.walletTabVouchers,
            },
            onSelected: (tab) => setState(() => _tab = tab),
          ),
          const SizedBox(height: Spacing.x3),
          NeoSwipeContent(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: switch (_tab) {
                _WalletTab.history when state.entries.isEmpty => [
                  NeoEmptyState(
                    icon: Icons.receipt_long_outlined,
                    message: l10n.walletNoHistory,
                    hint: l10n.walletNoHistoryHint,
                  ),
                ],
                // The design's "Son əməliyyatlar" (T-380): the latest few, and the
                // whole history behind "Hamısına bax".
                _WalletTab.history => [
                  NeoSectionHeader(
                    title: l10n.walletRecent,
                    actionLabel: state.entries.length > walletRecentCount
                        ? l10n.homeSeeAll
                        : null,
                    onAction: () => context.push(Routes.walletHistory),
                  ),
                  for (final entry in state.entries.take(walletRecentCount))
                    WalletEntryTile(entry: entry),
                ],
                _WalletTab.vouchers => [
                  Text(
                    l10n.walletVoucherHint,
                    style: theme.textTheme.bodyMedium,
                  ),
                  const SizedBox(height: Spacing.x3),
                  OutlinedButton.icon(
                    onPressed: () => showVoucherDialog(context),
                    icon: const Icon(Icons.confirmation_number_outlined),
                    label: Text(l10n.walletVoucherTitle),
                  ),
                ],
              },
            ),
          ),
        ],
      ),
    );
  }
}

/// The balance and the bonus pages on the brand's blue, with adding money
/// on it - the screen's one main action.
class _BalanceCard extends StatelessWidget {
  const _BalanceCard({required this.wallet});

  final Wallet wallet;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final language = Localizations.localeOf(context).languageCode;
    final onBrand = theme.colorScheme.onPrimary;
    final label = theme.textTheme.bodySmall?.copyWith(color: onBrand);

    return Container(
      padding: const EdgeInsets.all(Spacing.x4),
      decoration: BoxDecoration(
        color: theme.colorScheme.primary,
        borderRadius: BorderRadius.circular(Radii.lg),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(l10n.walletCurrentBalance, style: label),
                    const SizedBox(height: Spacing.x1),
                    Text(
                      formatMoney(wallet.balance, language: language),
                      style: theme.textTheme.headlineMedium?.copyWith(
                        color: onBrand,
                        fontWeight: FontWeights.semibold,
                        fontFeatures: const [FontFeature.tabularFigures()],
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(width: Spacing.x2),
              FilledButton.icon(
                style: FilledButton.styleFrom(
                  backgroundColor: onBrand,
                  foregroundColor: theme.colorScheme.primary,
                  minimumSize: const Size(0, Sizes.touchTarget),
                ),
                onPressed: () => showTopUpSheet(context),
                icon: const Icon(Icons.add),
                label: Text(l10n.walletTopUpShort),
              ),
            ],
          ),
          const SizedBox(height: Spacing.x3),
          Divider(height: 1, color: onBrand.withAlpha(64)),
          const SizedBox(height: Spacing.x3),
          Text(l10n.walletBonusLabel, style: label),
          const SizedBox(height: Spacing.x1),
          Text(
            l10n.homeBonusPagesShort(wallet.bonusPages),
            style: theme.textTheme.titleMedium?.copyWith(
              color: onBrand,
              fontWeight: FontWeights.semibold,
            ),
          ),
        ],
      ),
    );
  }
}

/// One movement of the balance - on the balance screen and in the whole
/// history (T-380).
class WalletEntryTile extends StatelessWidget {
  const WalletEntryTile({super.key, required this.entry});

  final WalletEntry entry;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final tokens = context.tokens;
    final theme = Theme.of(context);
    final language = Localizations.localeOf(context).languageCode;
    // Bonus pages without money (T-355): the pages are the amount.
    final pagesOnly = entry.amount == 0 && entry.bonusPages != 0;
    final incoming = pagesOnly ? entry.bonusPages > 0 : entry.amount > 0;
    final outgoing = pagesOnly ? entry.bonusPages < 0 : entry.amount < 0;
    final amount = pagesOnly
        ? l10n.homeBonusPagesShort(entry.bonusPages)
        : formatMoney(entry.amount, language: language);

    final (label, icon, tone) = switch (entry.type) {
      'TopUp' => (l10n.walletTypeTopUp, Icons.add_card, IconTileTone.brand),
      'Payment' => (
        l10n.walletTypePayment,
        Icons.print_outlined,
        IconTileTone.danger,
      ),
      'Refund' => (l10n.walletTypeRefund, Icons.undo, IconTileTone.info),
      'Bonus' => (
        l10n.walletTypeBonus,
        Icons.card_giftcard,
        IconTileTone.success,
      ),
      'VoucherRedeem' => (
        l10n.walletTypeVoucherRedeem,
        Icons.confirmation_number_outlined,
        IconTileTone.success,
      ),
      _ => (l10n.walletTypeAdminAdjust, Icons.tune, IconTileTone.warning),
    };

    final details = [
      if (entry.note?.isNotEmpty ?? false) entry.note!,
      if (entry.bonusPages != 0 && !pagesOnly)
        l10n.homeBonusPagesCount(entry.bonusPages),
      formatDateTime(
        entry.createdAt,
        today: l10n.commonToday,
        yesterday: l10n.commonYesterday,
      ),
    ];

    return ListTile(
      contentPadding: EdgeInsets.zero,
      leading: NeoIconTile(icon: icon, tone: tone),
      title: Text(label),
      // One quiet line, as the design has it: "08 Okt, 14:32 · KSK-024".
      subtitle: Text(
        details.join(' · '),
        maxLines: 2,
        overflow: TextOverflow.ellipsis,
        style: theme.textTheme.bodySmall?.copyWith(color: tokens.neutral600),
      ),
      trailing: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        crossAxisAlignment: CrossAxisAlignment.end,
        children: [
          Text(
            '${incoming ? '+' : ''}$amount',
            style: theme.textTheme.bodyLarge?.copyWith(
              color: incoming
                  ? tokens.success
                  : outgoing
                  ? tokens.danger
                  : tokens.neutral800,
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
Future<void> showVoucherDialog(BuildContext context, {String? code}) =>
    showDialog<void>(
      context: context,
      builder: (_) => _VoucherDialog(code: code),
    );

class _VoucherDialog extends ConsumerStatefulWidget {
  const _VoucherDialog({this.code});

  final String? code;

  @override
  ConsumerState<_VoucherDialog> createState() => _VoucherDialogState();
}

class _VoucherDialogState extends ConsumerState<_VoucherDialog> {
  late final _code = TextEditingController(text: widget.code);
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
