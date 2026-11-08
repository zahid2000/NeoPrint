import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/features/feature_flags.dart';
import '../../core/features/readiness.dart';
import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/router/app_router.dart';
import '../../core/router/back_stack.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_design.dart';
import '../../shared/widgets/neo_icon_tile.dart';
import '../files/file_sources.dart';
import '../jobs/job_card.dart';
import '../jobs/jobs_controller.dart';
import '../kiosk_search/kiosk_models.dart';
import '../kiosk_search/kiosk_photo.dart';
import '../kiosk_search/kiosk_status_view.dart';
import '../kiosk_search/user_location.dart';
import '../notifications/notifications_screen.dart';
import '../payments/scan_screen.dart';
import '../scanner/document_scanner.dart';
import '../wallet/wallet_controller.dart';
import 'near_kiosks.dart';

/// The home tab, as the design draws it (T-342; docs/DESIGN_SYSTEM.md,
/// "Mobil: yeni vizual dil"): the greeting; the nearest kiosk; four
/// shortcuts; the order on its way; the balance and the bonus; the other
/// kiosks around.
class HomeScreen extends ConsumerWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final canScanQr = Readiness.kioskPayment && ref.watch(canScanProvider);
    final jobs = Readiness.printJobs ? ref.watch(myJobsProvider) : null;
    final active = [...?jobs?.valueOrNull?.where((job) => job.isActive)];

    return Scaffold(
      appBar: AppBar(
        centerTitle: false,
        titleSpacing: Spacing.x4,
        title: Text(
          l10n.appTitle,
          style: theme.textTheme.titleLarge?.copyWith(
            color: tokens.brand500,
            fontWeight: FontWeights.semibold,
          ),
        ),
        actions: [
          // The kiosk's QR is read from here and from the Print tab.
          if (canScanQr)
            IconButton(
              tooltip: l10n.homeScanQr,
              onPressed: () => context.push(Routes.scan),
              icon: const Icon(Icons.qr_code_scanner),
            ),
          const NotificationBell(),
          const _Avatar(),
          const SizedBox(width: Spacing.x2),
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
          padding: const EdgeInsets.fromLTRB(
            Spacing.x4,
            Spacing.x2,
            Spacing.x4,
            Spacing.x6,
          ),
          children: [
            const _Greeting(),
            if (Readiness.kioskSearch) ...[
              const SizedBox(height: Spacing.x4),
              const _NearKioskCard(),
            ],
            const SizedBox(height: Spacing.x4),
            const _Shortcuts(),
            if (active.isNotEmpty) ...[
              const SizedBox(height: Spacing.x6),
              NeoSectionHeader(
                title: l10n.homeActiveOrder,
                actionLabel: l10n.homeSeeAll,
                onAction: () => context.push(Routes.jobs),
              ),
              for (final job in active.take(2)) ...[
                JobCard(job: job),
                const SizedBox(height: Spacing.x2),
              ],
            ] else if (jobs case AsyncError(:final error)) ...[
              const SizedBox(height: Spacing.x4),
              NeoBanner.error(
                message: l10n.homeJobsFailed(
                  error is ApiException
                      ? apiErrorText(l10n, error)
                      : l10n.errorServer,
                ),
              ),
            ],
            if (Readiness.wallet) ...[
              const SizedBox(height: Spacing.x4),
              const _MoneyRow(),
            ],
            if (Readiness.kioskSearch) const _NearbyKiosks(),
            // Every order, also the finished ones, one tap away when none is
            // on its way.
            if (Readiness.printJobs && active.isEmpty) ...[
              const SizedBox(height: Spacing.x4),
              NeoNavRow(
                icon: Icons.receipt_long_outlined,
                tone: IconTileTone.info,
                title: l10n.jobsTitle,
                hint: l10n.jobsHint,
                onTap: () => context.push(Routes.jobs),
              ),
            ],
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

    return Semantics(
      button: true,
      label: l10n.navProfile,
      child: InkWell(
        customBorder: const CircleBorder(),
        onTap: () => context.goTab(Routes.profile),
        child: SizedBox.square(
          dimension: Sizes.touchTarget,
          child: Center(
            child: CircleAvatar(
              radius: Spacing.x4 + 2,
              backgroundColor: tokens.brand100,
              child: Text(
                initialsOf(user?.fullName, user?.email),
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

/// "ZM" from "Zahid Mamedov", "DU" from "dev-user@...".
String initialsOf(String? fullName, String? email) {
  final name = (fullName?.trim().isNotEmpty ?? false)
      ? fullName!.trim()
      : (email ?? '');
  return name
      .split(RegExp(r'[\s@.\-_]+'))
      .where((part) => part.isNotEmpty)
      .take(2)
      .map((part) => part.characters.first.toUpperCase())
      .join();
}

class _Greeting extends ConsumerWidget {
  const _Greeting();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final user = ref.watch(currentUserProvider);
    final muted = context.tokens.neutral600;
    // The first name, as people are greeted.
    final first = user?.fullName?.trim().split(RegExp(r'\s+')).first;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          l10n.homeHello,
          style: theme.textTheme.bodyMedium?.copyWith(color: muted),
        ),
        Text(
          (first?.isNotEmpty ?? false)
              ? l10n.homeGreetingName(first!)
              : l10n.homeGreetingNoName,
          style: theme.textTheme.headlineSmall?.copyWith(
            fontWeight: FontWeights.semibold,
          ),
        ),
        const SizedBox(height: Spacing.x1),
        Text(
          l10n.homeHelloHint,
          style: theme.textTheme.bodySmall?.copyWith(color: muted),
        ),
      ],
    );
  }
}

/// The nearest kiosk on the brand's soft card, its picture on the right;
/// or what to do to see it.
class _NearKioskCard extends ConsumerWidget {
  const _NearKioskCard();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final near = ref.watch(nearKiosksProvider);

    return switch (near) {
      AsyncData(:final value) when value.kiosks.isNotEmpty => _KioskHero(
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
      _ => Container(
        height: 132,
        decoration: neoHeroDecoration(context),
        alignment: Alignment.center,
        child: const CircularProgressIndicator(),
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
    final tokens = context.tokens;

    return Container(
      decoration: neoHeroDecoration(context),
      padding: const EdgeInsets.all(Spacing.x4),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(Icons.place_outlined, size: 18, color: tokens.brand500),
              const SizedBox(width: Spacing.x1),
              Expanded(
                child: Text(
                  l10n.homeFindNearTitle,
                  style: theme.textTheme.titleMedium?.copyWith(
                    fontWeight: FontWeights.semibold,
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: Spacing.x1),
          Text(
            message,
            style: theme.textTheme.bodySmall?.copyWith(
              color: tokens.neutral600,
            ),
          ),
          const SizedBox(height: Spacing.x3),
          Wrap(
            spacing: Spacing.x2,
            runSpacing: Spacing.x2,
            children: [
              if (canAsk)
                OutlinedButton.icon(
                  onPressed: () => ref.read(nearKiosksProvider.notifier).find(),
                  icon: const Icon(Icons.my_location),
                  label: Text(l10n.homeShowNear),
                ),
              TextButton(
                onPressed: () => context.goTab(Routes.kiosks),
                child: Text(l10n.homeAllKiosks),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

/// "Yaxın kiosk · Central Mall · KSK-024 · 1-ci mərtəbə · Hazırdır · Növbə".
class _KioskHero extends StatelessWidget {
  const _KioskHero({required this.kiosk});

  final KioskSummary kiosk;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;

    return Material(
      color: tokens.brand50.withAlpha(0),
      child: Ink(
        decoration: neoHeroDecoration(context),
        child: InkWell(
          borderRadius: BorderRadius.circular(Radii.lg),
          onTap: () => context.push(Routes.kiosk(kiosk.code), extra: kiosk),
          child: Padding(
            padding: const EdgeInsets.all(Spacing.x4),
            child: Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          Icon(
                            Icons.place_outlined,
                            size: 16,
                            color: tokens.brand500,
                          ),
                          const SizedBox(width: Spacing.x1),
                          Flexible(
                            child: Text(
                              kiosk.distanceKm == null
                                  ? l10n.homeNearKiosk
                                  : '${l10n.homeNearKiosk} · '
                                        '${distanceText(context, kiosk.distanceKm!)}',
                              style: theme.textTheme.bodySmall?.copyWith(
                                color: tokens.neutral600,
                              ),
                            ),
                          ),
                        ],
                      ),
                      const SizedBox(height: Spacing.x1),
                      Text(
                        kiosk.name,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: theme.textTheme.titleLarge?.copyWith(
                          fontWeight: FontWeights.semibold,
                        ),
                      ),
                      Text(
                        [
                          kiosk.code,
                          if (kiosk.address.isNotEmpty) kiosk.address,
                        ].join(' · '),
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
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
                          if (kiosk.queueLength case final queue?)
                            _QueueText(queue: queue),
                        ],
                      ),
                    ],
                  ),
                ),
                const SizedBox(width: Spacing.x3),
                KioskPhoto(url: kiosk.photoUrl, size: 96),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// "Növbə: 3" with a people icon - how busy a kiosk is.
class _QueueText extends StatelessWidget {
  const _QueueText({required this.queue});

  final int queue;

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;

    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Icon(Icons.groups_outlined, size: 16, color: tokens.neutral600),
        const SizedBox(width: Spacing.x1),
        Text(
          queue == 0
              ? AppLocalizations.of(context).homeQueueEmpty
              : AppLocalizations.of(context).homeQueue(queue),
          style: Theme.of(context).textTheme.bodySmall
              ?.copyWith(color: tokens.neutral600),
        ),
      ],
    );
  }
}

/// Four shortcuts in one row, equal cards: "Fayldan çap et", "Skan et",
/// "Tərtibat yarat", "Fayl paylaş" - each goes on to printing.
class _Shortcuts extends ConsumerWidget {
  const _Shortcuts();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final sharing =
        Readiness.fileSharing &&
        ref.watch(featureOnProvider(FeatureKey.fileSharing));

    final tiles = [
      _Shortcut(
        icon: Icons.print_outlined,
        label: l10n.homePrintFromFile,
        onTap: () => context.goTab(Routes.printChoose),
      ),
      if (Readiness.documentScan && DocumentScanner.isSupportedPlatform)
        _Shortcut(
          icon: Icons.document_scanner_outlined,
          label: l10n.homeScanDocument,
          tone: IconTileTone.accent,
          onTap: () =>
              addFileFrom(context, ref, FileSource.scan, thenPrint: true),
        ),
      _Shortcut(
        icon: Icons.dashboard_customize_outlined,
        label: l10n.homeNewLayout,
        tone: IconTileTone.success,
        onTap: () => addFileFrom(context, ref, FileSource.layout),
      ),
      if (sharing)
        _Shortcut(
          icon: Icons.share_outlined,
          label: l10n.homeShareFile,
          tone: IconTileTone.warning,
          onTap: () => context.push(Routes.shares),
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
    this.tone = IconTileTone.brand,
  });

  final IconData icon;
  final String label;
  final VoidCallback onTap;
  final IconTileTone tone;

  @override
  Widget build(BuildContext context) {
    return Card(
      margin: EdgeInsets.zero,
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
              NeoIconTile(icon: icon, tone: tone),
              const SizedBox(height: Spacing.x2),
              Text(
                label,
                textAlign: TextAlign.center,
                maxLines: 2,
                style: Theme.of(context).textTheme.labelMedium,
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

    if (wallet case AsyncError(:final error)) {
      return NeoBanner.error(
        message: l10n.walletLoadFailed(
          error is ApiException ? apiErrorText(l10n, error) : l10n.errorServer,
        ),
      );
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
    final tokens = context.tokens;
    final colours = iconTileColours(context, tone);

    return Card(
      margin: EdgeInsets.zero,
      color: colours.background,
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: () => context.goTab(Routes.wallet),
        child: Padding(
          padding: const EdgeInsets.all(Spacing.x3),
          child: Row(
            children: [
              Icon(icon, color: colours.foreground),
              const SizedBox(width: Spacing.x2),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    Text(
                      label,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: tokens.neutral600,
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
                        style: theme.textTheme.titleMedium?.copyWith(
                          fontWeight: FontWeights.semibold,
                          fontFeatures: const [FontFeature.tabularFigures()],
                        ),
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

/// "Yaxınlıqdakı digər kiosklar": the kiosks after the nearest, sideways.
class _NearbyKiosks extends ConsumerWidget {
  const _NearbyKiosks();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final all = ref.watch(nearKiosksProvider).valueOrNull?.kiosks ?? const [];
    final kiosks = all.skip(1).toList();

    if (kiosks.isEmpty) {
      return const SizedBox.shrink();
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const SizedBox(height: Spacing.x6),
        NeoSectionHeader(
          title: l10n.homeNearbyKiosks,
          actionLabel: l10n.homeSeeAll,
          onAction: () => context.goTab(Routes.kiosks),
        ),
        SizedBox(
          height: 96,
          child: ListView.separated(
            scrollDirection: Axis.horizontal,
            itemCount: kiosks.length,
            separatorBuilder: (_, __) => const SizedBox(width: Spacing.x2),
            itemBuilder: (context, index) {
              final kiosk = kiosks[index];

              return SizedBox(
                width: 240,
                child: Card(
                  margin: EdgeInsets.zero,
                  clipBehavior: Clip.antiAlias,
                  child: InkWell(
                    onTap: () =>
                        context.push(Routes.kiosk(kiosk.code), extra: kiosk),
                    child: Padding(
                      padding: const EdgeInsets.all(Spacing.x2),
                      child: Row(
                        children: [
                          KioskPhoto(url: kiosk.photoUrl, size: 72),
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
                                    style: theme.textTheme.bodySmall?.copyWith(
                                      color: tokens.neutral600,
                                    ),
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
