import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/network/api_client.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_empty_state.dart';
import 'kiosk_map_view.dart';
import 'kiosk_models.dart';
import 'kiosk_search_controller.dart';
import 'kiosk_status_view.dart';
import 'map_tiles.dart';
import 'user_location.dart';

/// The kiosks tab (plan section 8.2, step 5): search by name, code or
/// address, "near me" with a radius, and the same result as a list or a map
/// (docs/DESIGN_SYSTEM.md, "Mobil: kiosklar").
class KioskSearchScreen extends ConsumerStatefulWidget {
  const KioskSearchScreen({super.key});

  @override
  ConsumerState<KioskSearchScreen> createState() => _KioskSearchScreenState();
}

class _KioskSearchScreenState extends ConsumerState<KioskSearchScreen> {
  /// The search goes when typing pauses, not on every letter.
  static const _debounce = Duration(milliseconds: 400);

  final _text = TextEditingController();
  Timer? _pending;

  @override
  void initState() {
    super.initState();
    _text.text = ref.read(kioskSearchProvider).text;
  }

  @override
  void dispose() {
    _pending?.cancel();
    _text.dispose();
    super.dispose();
  }

  void _typed(String value) {
    setState(() {});
    _pending?.cancel();
    _pending = Timer(
      _debounce,
      () => ref.read(kioskSearchProvider.notifier).setText(value),
    );
  }

  void _clear() {
    _pending?.cancel();
    _text.clear();
    setState(() {});
    ref.read(kioskSearchProvider.notifier).setText('');
  }

  void _open(KioskSummary kiosk) =>
      unawaited(context.push(Routes.kiosk(kiosk.code), extra: kiosk));

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final search = ref.watch(kioskSearchProvider);
    final controller = ref.read(kioskSearchProvider.notifier);
    final results = ref.watch(kioskResultsProvider);

    // Without a tile server for this build there is no map, only the list
    // (map_tiles.dart, T-061).
    final mapAvailable = MapTiles.url != null;
    final view = mapAvailable ? search.view : KioskView.list;

    return Scaffold(
      appBar: AppBar(title: Text(l10n.navKiosks)),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(
              Spacing.x4,
              Spacing.x2,
              Spacing.x4,
              0,
            ),
            child: TextField(
              controller: _text,
              onChanged: _typed,
              textInputAction: TextInputAction.search,
              decoration: InputDecoration(
                hintText: l10n.kioskSearchHint,
                prefixIcon: const Icon(Icons.search),
                suffixIcon: _text.text.isEmpty
                    ? null
                    : IconButton(
                        tooltip: l10n.kioskSearchClear,
                        icon: const Icon(Icons.close),
                        onPressed: _clear,
                      ),
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(
              Spacing.x4,
              Spacing.x3,
              Spacing.x4,
              0,
            ),
            child: Wrap(
              spacing: Spacing.x2,
              runSpacing: Spacing.x2,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                if (mapAvailable)
                  SegmentedButton<KioskView>(
                    segments: [
                      ButtonSegment(
                        value: KioskView.list,
                        icon: const Icon(Icons.view_list_outlined),
                        label: Text(l10n.kioskViewList),
                      ),
                      ButtonSegment(
                        value: KioskView.map,
                        icon: const Icon(Icons.map_outlined),
                        label: Text(l10n.kioskViewMap),
                      ),
                    ],
                    selected: {view},
                    onSelectionChanged: (selected) =>
                        controller.setView(selected.single),
                  ),
                FilterChip(
                  // Chosen, the chip shows its tick instead of the icon.
                  avatar: search.nearMe
                      ? null
                      : const Icon(Icons.near_me_outlined),
                  label: Text(l10n.kioskNearMe),
                  selected: search.nearMe,
                  onSelected: (on) => unawaited(controller.setNearMe(on)),
                ),
              ],
            ),
          ),
          if (search.nearMe) _NearMeBar(search: search),
          const SizedBox(height: Spacing.x2),
          Expanded(
            child: switch (results) {
              // A failure with nothing shown yet; a failed refresh keeps the list.
              AsyncError(:final error) when !results.hasValue => NeoEmptyState(
                icon: Icons.cloud_off_outlined,
                message: l10n.kioskLoadFailed(
                  error is ApiException
                      ? apiErrorText(l10n, error)
                      : l10n.errorServer,
                ),
                action: OutlinedButton(
                  onPressed: () => ref.invalidate(kioskResultsProvider),
                  child: Text(l10n.commonRetry),
                ),
              ),
              // While a new search loads, the last result stays on screen.
              AsyncValue(valueOrNull: final value?) =>
                value.isEmpty
                    ? _Empty(search: search, onClear: _clear)
                    : view == KioskView.map
                    ? KioskMapView(
                        kiosks: value,
                        me: search.near,
                        onOpen: _open,
                      )
                    : RefreshIndicator(
                        onRefresh: () =>
                            ref.refresh(kioskResultsProvider.future),
                        child: ListView.separated(
                          padding: const EdgeInsets.fromLTRB(
                            Spacing.x4,
                            Spacing.x2,
                            Spacing.x4,
                            Spacing.x6,
                          ),
                          itemCount: value.length,
                          separatorBuilder: (_, __) =>
                              const SizedBox(height: Spacing.x2),
                          itemBuilder: (_, index) => KioskCard(
                            kiosk: value[index],
                            onTap: () => _open(value[index]),
                          ),
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

/// The radius choice under "near me", or what stands in the way of it.
class _NearMeBar extends ConsumerWidget {
  const _NearMeBar({required this.search});

  final KioskSearchState search;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final controller = ref.read(kioskSearchProvider.notifier);

    if (search.locating) {
      return Padding(
        padding: const EdgeInsets.fromLTRB(
          Spacing.x4,
          Spacing.x3,
          Spacing.x4,
          0,
        ),
        child: Row(
          children: [
            const SizedBox.square(
              dimension: Spacing.x4,
              child: CircularProgressIndicator(strokeWidth: 2),
            ),
            const SizedBox(width: Spacing.x2),
            Text(l10n.kioskLocating),
          ],
        ),
      );
    }

    if (search.location case LocationMissing(:final problem)) {
      return Padding(
        padding: const EdgeInsets.fromLTRB(
          Spacing.x4,
          Spacing.x3,
          Spacing.x4,
          0,
        ),
        child: NeoBanner(
          tone: StatusTone.warning,
          icon: Icons.location_off_outlined,
          message: switch (problem) {
            LocationProblem.serviceOff => l10n.kioskLocationServiceOff,
            LocationProblem.denied => l10n.kioskLocationDenied,
            LocationProblem.deniedForever => l10n.kioskLocationDeniedForever,
            LocationProblem.unavailable => l10n.kioskLocationUnavailable,
          },
        ),
      );
    }

    return Padding(
      padding: const EdgeInsets.fromLTRB(Spacing.x4, Spacing.x2, Spacing.x4, 0),
      child: Wrap(
        spacing: Spacing.x2,
        children: [
          for (final km in kioskRadii)
            ChoiceChip(
              label: Text(l10n.kioskDistanceKm('$km')),
              selected: search.radiusKm == km,
              onSelected: (_) => controller.setRadius(km),
            ),
        ],
      ),
    );
  }
}

class _Empty extends ConsumerWidget {
  const _Empty({required this.search, required this.onClear});

  final KioskSearchState search;
  final VoidCallback onClear;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);

    if (search.text.isNotEmpty) {
      return NeoEmptyState(
        icon: Icons.search_off,
        message: l10n.kioskEmptySearch,
        hint: l10n.kioskEmptySearchHint,
        action: OutlinedButton(
          onPressed: onClear,
          child: Text(l10n.kioskSearchClear),
        ),
      );
    }

    if (search.near != null) {
      final widest = search.radiusKm >= kioskRadii.last;

      return NeoEmptyState(
        icon: Icons.location_searching,
        message: l10n.kioskEmptyNear(search.radiusKm),
        hint: l10n.kioskEmptyNearHint,
        action: widest
            ? null
            : OutlinedButton(
                onPressed: ref.read(kioskSearchProvider.notifier).widenRadius,
                child: Text(l10n.kioskWiderRadius),
              ),
      );
    }

    return NeoEmptyState(
      icon: Icons.place_outlined,
      message: l10n.kioskEmpty,
      hint: l10n.kioskEmptyHint,
    );
  }
}

/// One kiosk in the list: name, code and address, status and distance.
class KioskCard extends StatelessWidget {
  const KioskCard({super.key, required this.kiosk, required this.onTap});

  final KioskSummary kiosk;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final tokens = context.tokens;

    return Card(
      margin: EdgeInsets.zero,
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.all(Spacing.x4),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(kiosk.name, style: theme.textTheme.titleMedium),
                    const SizedBox(height: Spacing.x1),
                    Text(
                      [
                        kiosk.code,
                        if (kiosk.address.isNotEmpty) kiosk.address,
                      ].join(' · '),
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: tokens.neutral600,
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(width: Spacing.x2),
              Column(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  KioskStatusChip(status: kiosk.status),
                  if (kiosk.distanceKm case final km?) ...[
                    const SizedBox(height: Spacing.x1),
                    Text(
                      distanceText(context, km),
                      style: theme.textTheme.bodySmall?.copyWith(
                        fontFeatures: const [FontFeature.tabularFigures()],
                      ),
                    ),
                  ],
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}
