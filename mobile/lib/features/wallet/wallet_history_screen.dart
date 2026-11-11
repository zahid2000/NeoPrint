import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/theme/tokens.dart';
import '../../core/time/app_clock.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_design.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../../shared/widgets/neo_load_more.dart';
import 'wallet_controller.dart';
import 'wallet_screen.dart';

/// Which movements "Bütün əməliyyatlar" shows: this month first, then the
/// last three months, then dates the user picks (T-397, T-414 - no "all").
enum _Period { month, quarter, pick }

/// Every movement of the balance in a period, newest first and a page at a
/// time (T-413) - "Hamısına bax" under "Son əməliyyatlar" (T-380).
class WalletHistoryScreen extends ConsumerStatefulWidget {
  const WalletHistoryScreen({super.key});

  @override
  ConsumerState<WalletHistoryScreen> createState() =>
      _WalletHistoryScreenState();
}

class _WalletHistoryScreenState extends ConsumerState<WalletHistoryScreen> {
  _Period _period = _Period.month;

  /// The dates picked, the last one included.
  DateTimeRange? _picked;

  static DateTime _day(DateTime value) =>
      DateTime(value.year, value.month, value.day);

  WalletPeriod get _range {
    final today = _day(appNow());
    final tomorrow = today.add(const Duration(days: 1));
    final picked = _picked;
    return switch (_period) {
      _Period.quarter => (
        from: DateTime(today.year, today.month - 3, today.day),
        to: tomorrow,
      ),
      _Period.pick when picked != null => (
        from: _day(picked.start),
        to: _day(picked.end).add(const Duration(days: 1)),
      ),
      _ => (
        from: DateTime(today.year, today.month),
        to: DateTime(today.year, today.month + 1),
      ),
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
    final entries = ref.watch(walletPeriodProvider(range));
    final controller = ref.read(walletPeriodProvider(range).notifier);

    return Scaffold(
      appBar: neoTabAppBar(
        context,
        title: l10n.walletHistoryTitle,
        showBack: true,
      ),
      // Swiped sideways from period to period (T-432). "Tarix seç" is a
      // step of the swipe only once dates were picked: a swipe does not
      // open the calendar, a tap does.
      body: NeoTabSwipe<_Period>(
        values: [
          _Period.month,
          _Period.quarter,
          if (_picked != null) _Period.pick,
        ],
        selected: _period,
        onSelected: (period) => setState(() => _period = period),
        child: Column(
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
              child: NeoSwipeContent(
                child: switch (entries) {
                  AsyncData(:final value) => RefreshIndicator(
                    onRefresh: controller.refresh,
                    child: value.items.isEmpty
                        ? ListView(
                            children: [
                              const SizedBox(height: Spacing.x8),
                              NeoEmptyState(
                                icon: Icons.receipt_long_outlined,
                                message: l10n.walletPeriodEmpty,
                                hint: l10n.walletPeriodEmptyHint,
                              ),
                            ],
                          )
                        : NeoLoadMore(
                            paged: value,
                            onMore: controller.loadMore,
                            child: ListView.builder(
                              padding: const EdgeInsets.all(Spacing.x4),
                              itemCount: value.items.length + 1,
                              itemBuilder: (context, index) =>
                                  index < value.items.length
                                  ? WalletEntryTile(entry: value.items[index])
                                  : NeoLoadMoreFooter(
                                      paged: value,
                                      onRetry: () =>
                                          controller.loadMore(retry: true),
                                    ),
                            ),
                          ),
                  ),
                  AsyncError() => NeoEmptyState(
                    icon: Icons.cloud_off_outlined,
                    message: l10n.errorServer,
                    action: OutlinedButton(
                      onPressed: () =>
                          ref.invalidate(walletPeriodProvider(range)),
                      child: Text(l10n.commonRetry),
                    ),
                  ),
                  _ => const Center(child: CircularProgressIndicator()),
                },
              ),
            ),
          ],
        ),
      ),
    );
  }
}
