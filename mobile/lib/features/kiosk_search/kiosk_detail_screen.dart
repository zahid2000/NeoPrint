import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/features/feature_flags.dart';
import '../../core/features/readiness.dart';
import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../../shared/widgets/neo_primary_button.dart';
import '../session/session_controller.dart';
import 'kiosk_models.dart';
import 'kiosk_search_controller.dart';
import 'kiosk_status_view.dart';

/// One kiosk for a customer (plan section 8.2): status, what it can do, what
/// it costs, how busy it is and whether it has paper
/// (docs/DESIGN_SYSTEM.md, "Mobil: kiosklar").
///
/// "Sessiya aç" comes with session printing (step 5, M2); until then the
/// screen has no primary button rather than one that does nothing.
class KioskDetailScreen extends ConsumerWidget {
  const KioskDetailScreen({super.key, required this.idOrCode, this.initial});

  final String idOrCode;

  /// What the search already knew, so the title is there at once.
  final KioskSummary? initial;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final detail = ref.watch(kioskDetailProvider(idOrCode));

    return Scaffold(
      appBar: AppBar(
        // Opened from a link there is nothing behind it: back goes to the
        // kiosks tab rather than nowhere (docs/DESIGN_SYSTEM.md principle 5).
        leading: Navigator.of(context).canPop()
            ? null
            : BackButton(onPressed: () => context.go(Routes.kiosks)),
        title: Text(detail.valueOrNull?.summary.name ?? initial?.name ?? ''),
      ),
      bottomNavigationBar:
          Readiness.mobileSession &&
              ref.watch(featureOnProvider(FeatureKey.mobileSessionPrint))
          ? switch (detail) {
              AsyncData(:final value) => _SessionBar(kiosk: value),
              _ => null,
            }
          : null,
      body: switch (detail) {
        AsyncData(:final value) => RefreshIndicator(
          onRefresh: () => ref.refresh(kioskDetailProvider(idOrCode).future),
          child: _Detail(
            kiosk: value,
            // The search's distance; the detail has no "from where".
            distanceKm: initial?.distanceKm,
          ),
        ),
        AsyncError(:final error) when isNotFound(error) => NeoEmptyState(
          icon: Icons.location_off_outlined,
          message: l10n.kioskNotFound,
          hint: l10n.kioskNotFoundHint,
          action: OutlinedButton(
            onPressed: () => context.go(Routes.kiosks),
            child: Text(l10n.kioskShowAll),
          ),
        ),
        AsyncError(:final error) => NeoEmptyState(
          icon: Icons.cloud_off_outlined,
          message: l10n.kioskDetailLoadFailed(
            error is ApiException
                ? apiErrorText(l10n, error)
                : l10n.errorServer,
          ),
          action: OutlinedButton(
            onPressed: () => ref.invalidate(kioskDetailProvider(idOrCode)),
            child: Text(l10n.commonRetry),
          ),
        ),
        _ => const Center(child: CircularProgressIndicator()),
      },
    );
  }
}

class _Detail extends StatelessWidget {
  const _Detail({required this.kiosk, this.distanceKm});

  final KioskDetail kiosk;
  final double? distanceKm;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final l10n = AppLocalizations.of(context);
    final summary = kiosk.summary;
    final paper = [
      for (final size in kiosk.paper)
        if (size.state != PaperState.unknown) size,
    ];

    return ListView(
      padding: const EdgeInsets.fromLTRB(
        Spacing.x4,
        Spacing.x2,
        Spacing.x4,
        Spacing.x8,
      ),
      children: [
        Text(
          [
            summary.code,
            if (summary.address.isNotEmpty) summary.address,
          ].join(' · '),
          style: theme.textTheme.bodyMedium?.copyWith(color: tokens.neutral600),
        ),
        const SizedBox(height: Spacing.x3),
        Wrap(
          spacing: Spacing.x2,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            KioskStatusChip(status: summary.status),
            if (distanceKm case final km?)
              Text(distanceText(context, km), style: theme.textTheme.bodySmall),
          ],
        ),
        const SizedBox(height: Spacing.x4),
        _Section(
          title: l10n.kioskCapabilities,
          children: [
            _Capability(label: l10n.kioskCapColor, available: kiosk.color),
            _Capability(label: l10n.kioskCapDuplex, available: kiosk.duplex),
            _Capability(label: l10n.kioskCapA3, available: kiosk.a3),
          ],
        ),
        if (kiosk.prices case final prices?)
          _Section(
            title: l10n.kioskPrices,
            children: _prices(context, prices, a3: kiosk.a3),
          ),
        _Section(
          title: l10n.kioskQueue,
          children: [
            Text(
              kiosk.queueLength == 0
                  ? l10n.kioskQueueEmpty
                  : l10n.kioskQueueCount(kiosk.queueLength),
              style: theme.textTheme.bodyLarge,
            ),
          ],
        ),
        if (paper.isNotEmpty)
          _Section(
            title: l10n.kioskPaper,
            children: [for (final size in paper) _PaperRow(paper: size)],
          ),
      ],
    );
  }

  List<Widget> _prices(
    BuildContext context,
    KioskPrices prices, {
    required bool a3,
  }) {
    final l10n = AppLocalizations.of(context);
    final language = Localizations.localeOf(context).languageCode;
    final a3Times = prices.paperMultipliers['A3'];

    return [
      _PriceRow(
        label: l10n.kioskPriceBw,
        value: formatMoney(prices.bwPerSide, language: language),
      ),
      _PriceRow(
        label: l10n.kioskPriceColor,
        value: formatMoney(prices.colorPerSide, language: language),
      ),
      if (a3 && a3Times != null && a3Times != 1)
        _PriceRow(
          label: l10n.kioskPriceA3(formatNumber(a3Times, language: language)),
        ),
      _PriceRow(
        label: l10n.kioskPriceMinOrder,
        value: formatMoney(prices.minOrder, language: language),
      ),
      for (final tier in prices.tiers)
        _PriceRow(
          label: l10n.kioskPriceTier(
            tier.minSides,
            formatNumber(tier.discountPercent, language: language),
          ),
        ),
      const SizedBox(height: Spacing.x1),
      Text(l10n.kioskPricesHint, style: Theme.of(context).textTheme.bodySmall),
    ];
  }
}

class _Section extends StatelessWidget {
  const _Section({required this.title, required this.children});

  final String title;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: Spacing.x3),
    child: Card(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(title, style: Theme.of(context).textTheme.titleMedium),
            const SizedBox(height: Spacing.x2),
            ...children,
          ],
        ),
      ),
    ),
  );
}

/// What the kiosk can or cannot do: an icon and words, not colour alone.
class _Capability extends StatelessWidget {
  const _Capability({required this.label, required this.available});

  final String label;
  final bool available;

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;
    final l10n = AppLocalizations.of(context);

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: Spacing.x1),
      child: Row(
        children: [
          Icon(
            available
                ? Icons.check_circle_outline
                : Icons.remove_circle_outline,
            size: Spacing.x6,
            color: available
                ? tokens.forTone(StatusTone.success).foreground
                : tokens.neutral400,
          ),
          const SizedBox(width: Spacing.x2),
          Expanded(
            child: Text(
              available ? label : l10n.kioskCapMissing(label),
              style: Theme.of(context).textTheme.bodyLarge
                  ?.copyWith(color: available ? null : tokens.neutral600),
            ),
          ),
        ],
      ),
    );
  }
}

class _PriceRow extends StatelessWidget {
  const _PriceRow({required this.label, this.value});

  final String label;
  final String? value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: Spacing.x1),
      child: Row(
        children: [
          Expanded(child: Text(label, style: theme.textTheme.bodyLarge)),
          if (value case final value?)
            Text(
              value,
              style: theme.textTheme.bodyLarge?.copyWith(
                fontWeight: FontWeights.medium,
                fontFeatures: const [FontFeature.tabularFigures()],
              ),
            ),
        ],
      ),
    );
  }
}

/// One paper size: enough, low or out - with what "out" means for the order.
class _PaperRow extends StatelessWidget {
  const _PaperRow({required this.paper});

  final KioskPaper paper;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final (tone, icon, title, impact) = switch (paper.state) {
      PaperState.low => (
        StatusTone.warning,
        Icons.warning_amber_outlined,
        l10n.kioskPaperLow(paper.size),
        l10n.kioskPaperLowImpact,
      ),
      PaperState.out => (
        StatusTone.danger,
        Icons.error_outline,
        l10n.kioskPaperOut(paper.size),
        l10n.kioskPaperOutImpact(paper.size),
      ),
      _ => (
        StatusTone.success,
        Icons.check_circle_outline,
        l10n.kioskPaperOk(paper.size),
        null,
      ),
    };
    final colour = context.tokens.forTone(tone).foreground;

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: Spacing.x1),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(icon, size: Spacing.x6, color: colour),
          const SizedBox(width: Spacing.x2),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(title, style: theme.textTheme.bodyLarge),
                if (impact != null)
                  Text(impact, style: theme.textTheme.bodySmall),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// "Sessiya aç", or what to do in the session already open here
/// (docs/DESIGN_SYSTEM.md, "Mobil: sessiya ilə çap"). A kiosk that takes no
/// orders gets the reason instead of a button.
class _SessionBar extends ConsumerStatefulWidget {
  const _SessionBar({required this.kiosk});

  final KioskDetail kiosk;

  @override
  ConsumerState<_SessionBar> createState() => _SessionBarState();
}

class _SessionBarState extends ConsumerState<_SessionBar> {
  bool _busy = false;
  String? _error;

  Future<void> _open() async {
    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      await ref.read(sessionProvider.notifier).open(widget.kiosk.summary.code);
    } on ApiException catch (error) {
      if (mounted) {
        _error = AppLocalizations.of(
          context,
        ).sessionOpenFailed(apiErrorText(AppLocalizations.of(context), error));
      }
    } finally {
      if (mounted) {
        setState(() => _busy = false);
      }
    }
  }

  Future<void> _close() async {
    try {
      await ref.read(sessionProvider.notifier).close();
    } on ApiException catch (error) {
      if (mounted) {
        setState(
          () => _error = apiErrorText(AppLocalizations.of(context), error),
        );
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final session = ref.watch(sessionProvider).valueOrNull;
    final here = session != null && session.kioskId == widget.kiosk.summary.id;

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(
          Spacing.x4,
          Spacing.x2,
          Spacing.x4,
          Spacing.x4,
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (_error case final error?) ...[
              NeoBanner.error(message: error),
              const SizedBox(height: Spacing.x2),
            ],
            if (here) ...[
              NeoPrimaryButton(
                label: l10n.sessionChooseFile,
                icon: Icons.description_outlined,
                onPressed: () => unawaited(context.push(Routes.sessionFiles)),
              ),
              TextButton(
                onPressed: () => unawaited(_close()),
                child: Text(l10n.sessionClose),
              ),
            ] else if (widget.kiosk.summary.status == KioskStatus.online)
              NeoPrimaryButton(
                label: l10n.sessionOpen,
                icon: Icons.link,
                busy: _busy,
                onPressed: _busy ? null : () => unawaited(_open()),
              )
            else
              NeoBanner(
                tone: StatusTone.info,
                message: l10n.sessionKioskNotTaking,
              ),
          ],
        ),
      ),
    );
  }
}
