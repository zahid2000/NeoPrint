import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_design.dart';
import '../../shared/widgets/neo_empty_state.dart';
import 'wallet_controller.dart';
import 'wallet_screen.dart';

/// Every movement of the balance, newest first - "Hamısına bax" under "Son
/// əməliyyatlar" (T-380).
class WalletHistoryScreen extends ConsumerWidget {
  const WalletHistoryScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final wallet = ref.watch(walletProvider);

    return Scaffold(
      appBar: neoTabAppBar(
        context,
        title: l10n.walletHistoryTitle,
        showBack: true,
      ),
      body: switch (wallet) {
        AsyncData(:final value) => RefreshIndicator(
          onRefresh: () => ref.read(walletProvider.notifier).refresh(),
          child: value.entries.isEmpty
              ? ListView(
                  children: [
                    const SizedBox(height: Spacing.x8),
                    NeoEmptyState(
                      icon: Icons.receipt_long_outlined,
                      message: l10n.walletNoHistory,
                      hint: l10n.walletNoHistoryHint,
                    ),
                  ],
                )
              : ListView(
                  padding: const EdgeInsets.all(Spacing.x4),
                  children: [
                    for (final entry in value.entries)
                      WalletEntryTile(entry: entry),
                  ],
                ),
        ),
        AsyncError() => NeoEmptyState(
          icon: Icons.cloud_off_outlined,
          message: l10n.errorServer,
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
