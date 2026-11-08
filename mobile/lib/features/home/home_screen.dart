import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/features/entry_points.dart';
import '../../core/features/readiness.dart';
import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../../shared/widgets/neo_icon_tile.dart';
import '../files/file_sources.dart';
import '../jobs/job_card.dart';
import '../jobs/jobs_controller.dart';
import '../jobs/jobs_repository.dart';
import '../kiosk_search/kiosk_models.dart';
import '../kiosk_search/kiosk_photo.dart';
import '../kiosk_search/kiosk_status_view.dart';
import '../kiosk_search/user_location.dart';
import '../notifications/notifications_screen.dart';
import '../payments/scan_screen.dart';
import '../scanner/document_scanner.dart';
import '../wallet/wallet_controller.dart';
import 'near_kiosks.dart';

/// The home tab (docs/DESIGN_SYSTEM.md, "Mobil: yeni vizual dil", T-338):
/// a greeting; the jobs on their way, or the nearest kiosk when there are
/// none; the shortcuts; the money; the kiosks around; the recent jobs.
class HomeScreen extends ConsumerWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final personalQr = ref.watch(personalQrVisibleProvider);
    final jobs = Readiness.printJobs ? ref.watch(myJobsProvider) : null;
    final active = [...?jobs?.valueOrNull?.where((job) => job.isActive)];

    return Scaffold(
      appBar: AppBar(
        title: Text(l10n.appTitle),
        actions: const [
          NotificationBell(),
          _Avatar(),
          SizedBox(width: Spacing.x4),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: () async {
          await Future.wait([
            if (Readiness.printJobs)
              ref.read(myJobsProvider.notifier).refresh(),
            if (Readiness.wallet) ref.read(walletProvider.notifier).refresh(),
            if (Readiness.kioskSearch) ref.refresh(nearKiosksProvider.future),
          ]);
        },
        child: ListView(
          padding: const EdgeInsets.all(Spacing.x4),
          children: [
            const _Greeting(),
            const SizedBox(height: Spacing.x4),

            // The jobs on their way come first; with none, the kiosk nearby.
            if (active.isNotEmpty)
              _Section(
                title: l10n.homeActiveJobs,
                children: [for (final job in active) JobCard(job: job)],
              )
            else if (Readiness.kioskSearch)
              const _NearKioskCard(),

            const SizedBox(height: Spacing.x4),
            const _Shortcuts(),

            if (Readiness.wallet) ...[
              const SizedBox(height: Spacing.x4),
              const _MoneyRow(),
            ],

            // Printing with the personal QR (plan section 9.4).
            if (personalQr) ...[
              const SizedBox(height: Spacing.x4),
              NeoNavRow(
                icon: Icons.qr_code_2,
                title: l10n.myQrTitle,
                onTap: () => context.push(Routes.myQr),
              ),
              const SizedBox(height: Spacing.x2),
              NeoNavRow(
                icon: Icons.inventory_2_outlined,
                tone: IconTileTone.success,
                title: l10n.preparedTitle,
                onTap: () => context.push(Routes.prepared),
              ),
            ],

            if (Readiness.kioskSearch)
              _NearbyKiosks(skipNearest: active.isEmpty),

            if (jobs != null) _RecentJobs(jobs: jobs),
          ],
        ),
      ),
    );
  }
}

/// The user's initials; opens the profile tab.
class _Avatar extends ConsumerWidget {
  const _Avatar();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final user = ref.watch(currentUserProvider);
    final tokens = context.tokens;
    final name = (user?.fullName?.trim().isNotEmpty ?? false)
        ? user!.fullName!.trim()
        : (user?.email ?? '');
    final initials = name
        .split(RegExp(r'[\s@.]+'))
        .where((part) => part.isNotEmpty)
        .take(2)
        .map((part) => part.characters.first.toUpperCase())
        .join();

    return Semantics(
      button: true,
      label: l10n.navProfile,
      child: InkWell(
        customBorder: const CircleBorder(),
        onTap: () => context.go(Routes.profile),
        child: SizedBox.square(
          dimension: Sizes.touchTarget,
          child: Center(
            child: CircleAvatar(
              radius: Spacing.x4,
              backgroundColor: tokens.brand50,
              child: Text(
                initials,
                style: TextStyle(
                  color: tokens.brand500,
                  fontSize: FontSizes.caption,
                  fontWeight: FontWeights.semibold,
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _Greeting extends ConsumerWidget {
  const _Greeting();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final user = ref.watch(currentUserProvider);
    final muted = context.tokens.neutral600;
    final name = (user?.fullName?.trim().isNotEmpty ?? false)
        ? user!.fullName!.trim()
        : null;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          l10n.homeHello,
          style: theme.textTheme.bodySmall?.copyWith(color: muted),
        ),
        if (name != null) Text(name, style: theme.textTheme.headlineSmall),
        const SizedBox(height: Spacing.x1),
        Text(
          l10n.homeHelloHint,
          style: theme.textTheme.bodyMedium?.copyWith(color: muted),
        ),
      ],
    );
  }
}

class _Section extends StatelessWidget {
  const _Section({required this.title, required this.children});

  final String title;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      Text(title, style: Theme.of(context).textTheme.titleMedium),
      const SizedBox(height: Spacing.x2),
      for (final child in children) ...[
        child,
        const SizedBox(height: Spacing.x2),
      ],
    ],
  );
}

/// The nearest kiosk, or what to do to see it.
class _NearKioskCard extends ConsumerWidget {
  const _NearKioskCard();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final near = ref.watch(nearKiosksProvider);

    return switch (near) {
      AsyncData(:final value) when value.kiosks.isNotEmpty => _KioskCard(
        kiosk: value.kiosks.first,
      ),
      AsyncData(:final value) => _NoNearKiosk(
        message: switch (value.problem) {
          null => l10n.homeNoNearKiosk,
          LocationProblem.notAsked => l10n.homeFindNearHint,
          _ => l10n.homeNoLocation,
        },
        canAsk: value.problem == LocationProblem.notAsked,
      ),
      AsyncError() => _NoNearKiosk(message: l10n.homeNoLocation, canAsk: false),
      _ => const Card(
        child: Padding(
          padding: EdgeInsets.all(Spacing.x4),
          child: LinearProgressIndicator(),
        ),
      ),
    };
  }
}

class _NoNearKiosk extends ConsumerWidget {
  const _NoNearKiosk({required this.message, required this.canAsk});

  final String message;

  /// The permission was not asked for yet: the button asks.
  final bool canAsk;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                const NeoIconTile(icon: Icons.place_outlined),
                const SizedBox(width: Spacing.x3),
                Expanded(
                  child: Text(
                    l10n.homeFindNearTitle,
                    style: theme.textTheme.titleMedium,
                  ),
                ),
              ],
            ),
            const SizedBox(height: Spacing.x2),
            Text(message, style: theme.textTheme.bodyMedium),
            const SizedBox(height: Spacing.x3),
            if (canAsk) ...[
              OutlinedButton.icon(
                onPressed: () => ref.read(nearKiosksProvider.notifier).find(),
                icon: const Icon(Icons.my_location),
                label: Text(l10n.homeShowNear),
              ),
              const SizedBox(height: Spacing.x1),
            ],
            TextButton(
              onPressed: () => context.go(Routes.kiosks),
              child: Text(l10n.homeAllKiosks),
            ),
          ],
        ),
      ),
    );
  }
}

/// The nearest kiosk: its picture, name, code and address, status and
/// distance; the card opens the kiosk.
class _KioskCard extends StatelessWidget {
  const _KioskCard({required this.kiosk});

  final KioskSummary kiosk;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;

    return Card(
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: () => context.push(Routes.kiosk(kiosk.code), extra: kiosk),
        child: Padding(
          padding: const EdgeInsets.all(Spacing.x3),
          child: Row(
            children: [
              const KioskPhoto(),
              const SizedBox(width: Spacing.x3),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Icon(
                          Icons.place_outlined,
                          size: 14,
                          color: tokens.brand500,
                        ),
                        const SizedBox(width: Spacing.x1),
                        Flexible(
                          child: Text(
                            l10n.homeNearKiosk,
                            style: theme.textTheme.bodySmall?.copyWith(
                              color: tokens.neutral600,
                            ),
                          ),
                        ),
                      ],
                    ),
                    Text(kiosk.name, style: theme.textTheme.titleMedium),
                    Text(
                      [
                        kiosk.code,
                        if (kiosk.address.isNotEmpty) kiosk.address,
                      ].join(' · '),
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: tokens.neutral600,
                      ),
                    ),
                    const SizedBox(height: Spacing.x2),
                    Wrap(
                      spacing: Spacing.x2,
                      runSpacing: Spacing.x1,
                      crossAxisAlignment: WrapCrossAlignment.center,
                      children: [
                        KioskStatusChip(status: kiosk.status),
                        if (kiosk.distanceKm case final km?)
                          Text(
                            distanceText(context, km),
                            style: theme.textTheme.bodySmall?.copyWith(
                              fontFeatures: const [
                                FontFeature.tabularFigures(),
                              ],
                            ),
                          ),
                      ],
                    ),
                  ],
                ),
              ),
              Icon(Icons.chevron_right, color: tokens.neutral400),
            ],
          ),
        ),
      ),
    );
  }
}

/// Up to four shortcuts in one row; the screen's one main action is filled.
class _Shortcuts extends ConsumerWidget {
  const _Shortcuts();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final canScanQr = Readiness.kioskPayment && ref.watch(canScanProvider);
    final scheduled = ref.watch(scheduledVisibleProvider);

    final tiles = [
      if (canScanQr)
        _Shortcut(
          icon: Icons.qr_code_scanner,
          label: l10n.homeScanQr,
          primary: true,
          onTap: () => context.push(Routes.scan),
        ),
      _Shortcut(
        icon: Icons.upload_file_outlined,
        label: l10n.homeAddFile,
        // Without a scanner, adding a file is the main action.
        primary: !canScanQr,
        onTap: () => context.go(Routes.print),
      ),
      if (Readiness.documentScan && DocumentScanner.isSupportedPlatform)
        _Shortcut(
          icon: Icons.document_scanner_outlined,
          label: l10n.homeScanDocument,
          onTap: () => addFileFrom(context, ref, FileSource.scan),
        ),
      if (scheduled)
        _Shortcut(
          icon: Icons.schedule_outlined,
          label: l10n.homeScheduled,
          tone: IconTileTone.warning,
          onTap: () => context.go(Routes.scheduled),
        ),
    ];

    return IntrinsicHeight(
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          for (var i = 0; i < tiles.length; i++) ...[
            if (i > 0) const SizedBox(width: Spacing.x2),
            Expanded(child: tiles[i]),
          ],
        ],
      ),
    );
  }
}

class _Shortcut extends StatelessWidget {
  const _Shortcut({
    required this.icon,
    required this.label,
    required this.onTap,
    this.primary = false,
    this.tone = IconTileTone.brand,
  });

  final IconData icon;
  final String label;
  final VoidCallback onTap;
  final bool primary;
  final IconTileTone tone;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final text = Theme.of(context).textTheme.labelMedium;

    return Card(
      color: primary ? scheme.primary : null,
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.symmetric(
            horizontal: Spacing.x1,
            vertical: Spacing.x3,
          ),
          child: Column(
            children: [
              if (primary)
                Icon(icon, size: 28, color: scheme.onPrimary)
              else
                NeoIconTile(icon: icon, tone: tone),
              const SizedBox(height: Spacing.x2),
              Text(
                label,
                textAlign: TextAlign.center,
                maxLines: 2,
                style: text?.copyWith(color: primary ? scheme.onPrimary : null),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// The balance and the bonus pages, side by side; both open the wallet tab.
class _MoneyRow extends ConsumerWidget {
  const _MoneyRow();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final wallet = ref.watch(walletProvider);
    final language = Localizations.localeOf(context).languageCode;

    String? failure;
    if (wallet case AsyncError(:final error)) {
      failure = l10n.walletLoadFailed(
        error is ApiException ? apiErrorText(l10n, error) : l10n.errorServer,
      );
    }

    if (failure != null) {
      return NeoBanner.error(message: failure);
    }

    final value = wallet.valueOrNull?.wallet;

    return IntrinsicHeight(
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Expanded(
            child: _MoneyCard(
              icon: Icons.account_balance_wallet_outlined,
              label: l10n.homeBalance,
              value: value == null
                  ? null
                  : formatMoney(value.balance, language: language),
            ),
          ),
          const SizedBox(width: Spacing.x2),
          Expanded(
            child: _MoneyCard(
              icon: Icons.card_giftcard_outlined,
              tone: IconTileTone.success,
              label: l10n.homeBonus,
              value: value == null
                  ? null
                  : l10n.homeBonusPagesShort(value.bonusPages),
            ),
          ),
        ],
      ),
    );
  }
}

class _MoneyCard extends StatelessWidget {
  const _MoneyCard({
    required this.icon,
    required this.label,
    required this.value,
    this.tone = IconTileTone.brand,
  });

  final IconData icon;
  final String label;

  /// Null while loading.
  final String? value;
  final IconTileTone tone;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Card(
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: () => context.go(Routes.wallet),
        child: Padding(
          padding: const EdgeInsets.all(Spacing.x3),
          child: Row(
            children: [
              NeoIconTile(icon: icon, tone: tone),
              const SizedBox(width: Spacing.x2),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    Text(
                      label,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: context.tokens.neutral600,
                      ),
                    ),
                    if (value == null)
                      const Padding(
                        padding: EdgeInsets.only(top: Spacing.x2),
                        child: LinearProgressIndicator(),
                      )
                    else
                      Text(
                        value!,
                        style: theme.textTheme.titleLarge?.copyWith(
                          fontWeight: FontWeights.semibold,
                          fontFeatures: const [FontFeature.tabularFigures()],
                        ),
                      ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// The other kiosks around, sideways; only with a location.
class _NearbyKiosks extends ConsumerWidget {
  const _NearbyKiosks({required this.skipNearest});

  /// The nearest is already the card at the top.
  final bool skipNearest;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final all = ref.watch(nearKiosksProvider).valueOrNull?.kiosks ?? const [];
    final kiosks = skipNearest ? all.skip(1).toList() : all;

    if (kiosks.isEmpty) {
      return const SizedBox.shrink();
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const SizedBox(height: Spacing.x6),
        Text(l10n.homeNearbyKiosks, style: theme.textTheme.titleMedium),
        const SizedBox(height: Spacing.x2),
        SizedBox(
          height: 96,
          child: ListView.separated(
            scrollDirection: Axis.horizontal,
            itemCount: kiosks.length,
            separatorBuilder: (_, __) => const SizedBox(width: Spacing.x2),
            itemBuilder: (context, index) {
              final kiosk = kiosks[index];

              return SizedBox(
                width: 232,
                child: Card(
                  clipBehavior: Clip.antiAlias,
                  child: InkWell(
                    onTap: () =>
                        context.push(Routes.kiosk(kiosk.code), extra: kiosk),
                    child: Padding(
                      padding: const EdgeInsets.all(Spacing.x2),
                      child: Row(
                        children: [
                          const KioskPhoto(size: 64),
                          const SizedBox(width: Spacing.x2),
                          Expanded(
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              mainAxisAlignment: MainAxisAlignment.center,
                              children: [
                                Text(
                                  kiosk.name,
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: theme.textTheme.titleSmall,
                                ),
                                if (kiosk.distanceKm case final km?)
                                  Text(
                                    distanceText(context, km),
                                    style: theme.textTheme.bodySmall,
                                  ),
                                const SizedBox(height: Spacing.x1),
                                KioskStatusChip(status: kiosk.status),
                              ],
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
                ),
              );
            },
          ),
        ),
      ],
    );
  }
}

/// The finished jobs, or the empty state when there are none at all.
class _RecentJobs extends StatelessWidget {
  const _RecentJobs({required this.jobs});

  final AsyncValue<List<MyJob>> jobs;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    return switch (jobs) {
      AsyncData(:final value) when value.isEmpty => NeoEmptyState(
        icon: Icons.receipt_long_outlined,
        message: l10n.homeNoJobs,
        hint: l10n.homeNoJobsHint,
      ),
      AsyncData(:final value) when value.any((job) => !job.isActive) => Padding(
        padding: const EdgeInsets.only(top: Spacing.x6),
        child: _Section(
          title: l10n.homeRecentJobs,
          children: [
            for (final job in value.where((job) => !job.isActive).take(10))
              JobCard(job: job),
          ],
        ),
      ),
      AsyncData() => const SizedBox.shrink(),
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
