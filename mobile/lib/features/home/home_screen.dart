import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/features/readiness.dart';
import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../jobs/job_card.dart';
import '../jobs/jobs_controller.dart';
import '../jobs/jobs_repository.dart';
import '../payments/scan_screen.dart';
import '../wallet/wallet_controller.dart';

/// The home tab (plan section 8.2; docs/DESIGN_SYSTEM.md 5.2): the balance
/// first, the one main action under it, then the jobs still on their way and
/// the recent ones.
class HomeScreen extends ConsumerWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final user = ref.watch(currentUserProvider);
    final canScan = Readiness.kioskPayment && ref.watch(canScanProvider);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.appTitle)),
      body: RefreshIndicator(
        onRefresh: () async {
          await Future.wait([
            if (Readiness.printJobs)
              ref.read(myJobsProvider.notifier).refresh(),
            if (Readiness.wallet) ref.read(walletProvider.notifier).refresh(),
          ]);
        },
        child: ListView(
          padding: const EdgeInsets.all(Spacing.x4),
          children: [
            if (Readiness.wallet)
              const _BalanceCard()
            else
              _GreetingCard(name: user?.fullName ?? user?.email ?? ''),
            const SizedBox(height: Spacing.x4),

            // One primary action: scanning where the phone can, the files
            // where it cannot (the web build has no scanner).
            if (canScan) ...[
              FilledButton.icon(
                onPressed: () => context.push(Routes.scan),
                icon: const Icon(Icons.qr_code_scanner),
                label: Text(l10n.homeScanQr),
              ),
              const SizedBox(height: Spacing.x2),
              OutlinedButton.icon(
                onPressed: () => context.go(Routes.files),
                icon: const Icon(Icons.folder_outlined),
                label: Text(l10n.homeOpenFiles),
              ),
            ] else
              FilledButton.icon(
                onPressed: () => context.go(Routes.files),
                icon: const Icon(Icons.folder_outlined),
                label: Text(l10n.homeOpenFiles),
              ),

            // Printing with the personal QR (plan section 9.4).
            if (Readiness.personalQr) ...[
              const SizedBox(height: Spacing.x2),
              Row(
                children: [
                  Expanded(
                    child: OutlinedButton.icon(
                      onPressed: () => context.push(Routes.myQr),
                      icon: const Icon(Icons.qr_code_2),
                      label: Text(l10n.myQrTitle),
                    ),
                  ),
                  const SizedBox(width: Spacing.x2),
                  Expanded(
                    child: OutlinedButton.icon(
                      onPressed: () => context.push(Routes.prepared),
                      icon: const Icon(Icons.inventory_2_outlined),
                      label: Text(l10n.preparedTitle),
                    ),
                  ),
                ],
              ),
            ],

            if (Readiness.printJobs) const _Jobs(),
          ],
        ),
      ),
    );
  }
}

class _GreetingCard extends StatelessWidget {
  const _GreetingCard({required this.name});

  final String name;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);

    return Card(
      color: theme.colorScheme.primaryContainer,
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              name,
              style: theme.textTheme.titleMedium?.copyWith(
                color: theme.colorScheme.onPrimaryContainer,
              ),
            ),
            const SizedBox(height: Spacing.x2),
            Text(
              l10n.homeFilesHint,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: theme.colorScheme.onPrimaryContainer,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// The balance and the bonus pages; the whole card opens the wallet.
class _BalanceCard extends ConsumerWidget {
  const _BalanceCard();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final wallet = ref.watch(walletProvider);
    final onCard = theme.colorScheme.onPrimaryContainer;

    return Card(
      color: theme.colorScheme.primaryContainer,
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: () => context.push(Routes.wallet),
        child: Padding(
          padding: const EdgeInsets.all(Spacing.x4),
          child: Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      l10n.homeBalance,
                      style: theme.textTheme.bodyMedium?.copyWith(
                        color: onCard,
                      ),
                    ),
                    const SizedBox(height: Spacing.x1),
                    switch (wallet) {
                      AsyncData(:final value) => Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            formatMoney(
                              value.wallet.balance,
                              language: Localizations.localeOf(context)
                                  .languageCode,
                            ),
                            style: theme.textTheme.headlineMedium?.copyWith(
                              color: onCard,
                              fontWeight: FontWeights.semibold,
                              fontFeatures: const [
                                FontFeature.tabularFigures(),
                              ],
                            ),
                          ),
                          if (value.wallet.bonusPages > 0)
                            Text(
                              l10n.homeBonusPagesCount(value.wallet.bonusPages),
                              style: theme.textTheme.bodySmall?.copyWith(
                                color: onCard,
                              ),
                            ),
                        ],
                      ),
                      AsyncError(:final error) => Text(
                        l10n.walletLoadFailed(
                          error is ApiException
                              ? apiErrorText(l10n, error)
                              : l10n.errorServer,
                        ),
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: onCard,
                        ),
                      ),
                      _ => const Padding(
                        padding: EdgeInsets.symmetric(vertical: Spacing.x2),
                        child: LinearProgressIndicator(),
                      ),
                    },
                  ],
                ),
              ),
              Icon(Icons.chevron_right, color: onCard),
            ],
          ),
        ),
      ),
    );
  }
}

/// The jobs on their way above, the history below.
class _Jobs extends ConsumerWidget {
  const _Jobs();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final jobs = ref.watch(myJobsProvider);

    Widget section(String title, List<MyJob> items) => Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const SizedBox(height: Spacing.x6),
        Text(title, style: theme.textTheme.titleMedium),
        const SizedBox(height: Spacing.x2),
        for (final job in items) ...[
          JobCard(job: job),
          const SizedBox(height: Spacing.x2),
        ],
      ],
    );

    return switch (jobs) {
      AsyncData(:final value) when value.isEmpty => NeoEmptyState(
        icon: Icons.receipt_long_outlined,
        message: l10n.homeNoJobs,
        hint: l10n.homeNoJobsHint,
      ),
      AsyncData(:final value) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          if (value.any((job) => job.isActive))
            section(
              l10n.homeActiveJobs,
              value.where((job) => job.isActive).toList(),
            ),
          if (value.any((job) => !job.isActive))
            section(
              l10n.homeRecentJobs,
              value.where((job) => !job.isActive).take(10).toList(),
            ),
        ],
      ),
      AsyncError(:final error) => Padding(
        padding: const EdgeInsets.only(top: Spacing.x6),
        child: NeoBanner.error(
          message: l10n.homeJobsFailed(
            error is ApiException
                ? apiErrorText(l10n, error)
                : l10n.errorServer,
          ),
        ),
      ),
      _ => const Padding(
        padding: EdgeInsets.only(top: Spacing.x6),
        child: Center(child: CircularProgressIndicator()),
      ),
    };
  }
}
