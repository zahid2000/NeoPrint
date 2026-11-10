import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/theme/tokens.dart';
import '../../core/time/app_clock.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_design.dart';
import '../../shared/widgets/neo_empty_state.dart';
import 'wallet_controller.dart';
import 'wallet_repository.dart';
import 'wallet_screen.dart';

/// Which movements "Bütün əməliyyatlar" shows (T-397).
enum _Period { all, month, quarter, pick }

/// Every movement of the balance, newest first - "Hamısına bax" under "Son
/// əməliyyatlar" (T-380) - or those of a period: this month, the last three
/// months, or dates the user picks (T-397).
class WalletHistoryScreen extends ConsumerStatefulWidget {
  const WalletHistoryScreen({super.key});

  @override
  ConsumerState<WalletHistoryScreen> createState() =>
      _WalletHistoryScreenState();
}

class _WalletHistoryScreenState extends ConsumerState<WalletHistoryScreen> {
  _Period _period = _Period.all;

  /// The dates picked, the last one included.
  DateTimeRange? _picked;

  static DateTime _day(DateTime value) =>
      DateTime(value.year, value.month, value.day);

  WalletPeriod? get _range {
    final today = _day(appNow());
    final tomorrow = today.add(const Duration(days: 1));
    return switch (_period) {
      _Period.all => null,
      _Period.month => (
        from: DateTime(today.year, today.month),
        to: DateTime(today.year, today.month + 1),
      ),
      _Period.quarter => (
        from: DateTime(today.year, today.month - 3, today.day),
        to: tomorrow,
      ),
      _Period.pick when _picked != null => (
        from: _day(_picked!.start),
        to: _day(_picked!.end).add(const Duration(days: 1)),
      ),
      _Period.pick => null,
    };
  }

  Future<void> _choose(_Period period) async {
    if (period != _Period.pick) {
      setState(() => _period = period);
      return;
    }

    final today = _day(appNow());
    final picked = await showDateRangePicker(
      context: context,
      firstDate: DateTime(today.year - 5),
      lastDate: today,
      initialDateRange: _picked,
    );
    if (picked != null && mounted) {
      setState(() {
        _picked = picked;
        _period = _Period.pick;
      });
    }
  }

  String _label(AppLocalizations l10n, _Period period) => switch (period) {
    _Period.all => l10n.walletPeriodAll,
    _Period.month => l10n.walletPeriodMonth,
    _Period.quarter => l10n.walletPeriodQuarter,
    _Period.pick when _picked != null =>
      '${_short(_picked!.start)} – ${_short(_picked!.end)}',
    _Period.pick => l10n.walletPeriodPick,
  };

  static String _short(DateTime day) =>
      '${day.day.toString().padLeft(2, '0')}.'
      '${day.month.toString().padLeft(2, '0')}';

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final range = _range;
    final AsyncValue<List<WalletEntry>> entries = range == null
        ? ref.watch(walletProvider).whenData((value) => value.entries)
        : ref.watch(walletPeriodProvider(range));

    Future<void> refresh() => range == null
        ? ref.read(walletProvider.notifier).refresh()
        : ref.refresh(walletPeriodProvider(range).future);

    return Scaffold(
      appBar: neoTabAppBar(
        context,
        title: l10n.walletHistoryTitle,
        showBack: true,
      ),
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(
              Spacing.x4,
              Spacing.x2,
              Spacing.x4,
              0,
            ),
            child: NeoPillTabs<_Period>(
              values: _Period.values,
              selected: _period,
              label: (period) => _label(l10n, period),
              onSelected: _choose,
            ),
          ),
          Expanded(
            child: switch (entries) {
              AsyncData(:final value) => RefreshIndicator(
                onRefresh: refresh,
                child: value.isEmpty
                    ? ListView(
                        children: [
                          const SizedBox(height: Spacing.x8),
                          NeoEmptyState(
                            icon: Icons.receipt_long_outlined,
                            message: range == null
                                ? l10n.walletNoHistory
                                : l10n.walletPeriodEmpty,
                            hint: range == null
                                ? l10n.walletNoHistoryHint
                                : l10n.walletPeriodEmptyHint,
                          ),
                        ],
                      )
                    : ListView(
                        padding: const EdgeInsets.all(Spacing.x4),
                        children: [
                          for (final entry in value)
                            WalletEntryTile(entry: entry),
                        ],
                      ),
              ),
              AsyncError() => NeoEmptyState(
                icon: Icons.cloud_off_outlined,
                message: l10n.errorServer,
                action: OutlinedButton(
                  onPressed: () => range == null
                      ? ref.invalidate(walletProvider)
                      : ref.invalidate(walletPeriodProvider(range)),
                  child: Text(l10n.commonRetry),
                ),
              ),
              _ => const Center(child: CircularProgressIndicator()),
            },
          ),
        ],
      ),
    );
  }
}
