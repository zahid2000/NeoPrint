import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/platform/browser.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import 'kiosk_models.dart';
import 'kiosk_repository.dart';
import 'kiosk_search_screen.dart';
import 'kiosk_status_view.dart';
import 'map/kiosk_map_engine.dart';
import 'map/map_config.dart';

/// How long the map's style may take before the list takes its place (T-286).
const mapLoadTimeout = Duration(seconds: 8);

/// The search result on the OpenFreeMap map (docs/DESIGN_SYSTEM.md, "Mobil:
/// kiosklar"; T-217): a marker per kiosk in its status colour, a legend below,
/// the attribution always in sight with every part a link, and a card for the
/// kiosk tapped. An alternative to the list, never more or fewer kiosks than it.
///
/// A map that does not load gives way to the same kiosks as a list, with a
/// word of why and a retry; each opening of the map tries again.
class KioskMapView extends ConsumerStatefulWidget {
  const KioskMapView({
    super.key,
    required this.kiosks,
    required this.onOpen,
    this.me,
  });

  final List<KioskSummary> kiosks;
  final ValueChanged<KioskSummary> onOpen;

  /// Where the user is, when "near me" found it.
  final GeoPoint? me;

  @override
  ConsumerState<KioskMapView> createState() => _KioskMapViewState();
}

enum _MapState { loading, ready, failed }

class _KioskMapViewState extends ConsumerState<KioskMapView> {
  KioskSummary? _selected;
  _MapState _state = _MapState.loading;
  Timer? _timeout;

  /// A new attempt is a new map widget, so a map stuck loading is replaced.
  int _attempt = 0;

  @override
  void initState() {
    super.initState();
    _startTimeout();
  }

  @override
  void dispose() {
    _timeout?.cancel();
    super.dispose();
  }

  void _startTimeout() {
    _timeout?.cancel();
    _timeout = Timer(mapLoadTimeout, () {
      if (mounted && _state == _MapState.loading) {
        setState(() => _state = _MapState.failed);
      }
    });
  }

  void _retry() {
    ref.invalidate(mapConfigProvider);
    setState(() {
      _state = _MapState.loading;
      _attempt++;
    });
    _startTimeout();
  }

  void _loaded() {
    _timeout?.cancel();
    if (mounted && _state != _MapState.ready) {
      setState(() => _state = _MapState.ready);
    }
  }

  @override
  void didUpdateWidget(KioskMapView old) {
    super.didUpdateWidget(old);

    // A kiosk that is no longer in the result is no longer selected.
    if (_selected != null && !widget.kiosks.any((k) => k.id == _selected!.id)) {
      _selected = null;
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final engine = ref.watch(kioskMapEngineProvider);

    if (!engine.available) {
      return _Fallback(
        kiosks: widget.kiosks,
        onOpen: widget.onOpen,
        title: l10n.mapUnavailableTitle,
        body: l10n.mapUnavailableBody,
      );
    }

    final config = ref.watch(mapConfigProvider);

    // The settings did not come either: the same fallback as a map that did
    // not load.
    if (_state == _MapState.failed || config.hasError) {
      _timeout?.cancel();
      return _Fallback(
        kiosks: widget.kiosks,
        onOpen: widget.onOpen,
        title: l10n.mapFailedTitle,
        body: l10n.mapFailedBody,
        onRetry: _retry,
      );
    }

    final tokens = context.tokens;
    final settings = config.valueOrNull;

    return Column(
      children: [
        Expanded(
          child: Stack(
            children: [
              if (settings != null)
                KeyedSubtree(
                  key: ValueKey(_attempt),
                  child: engine.build(
                    context,
                    MapRequest(
                      styleUrl: settings.styleUrl,
                      pins: [
                        for (final kiosk in widget.kiosks)
                          MapPin(
                            id: kiosk.id,
                            lat: kiosk.lat,
                            lng: kiosk.lng,
                            colour: tokens
                                .forTone(kiosk.status.tone)
                                .foreground,
                            label: '${kiosk.name}, ${kiosk.status.label(l10n)}',
                            selected: _selected?.id == kiosk.id,
                          ),
                        if (widget.me case final me?)
                          MapPin(
                            id: 'me',
                            lat: me.lat,
                            lng: me.lng,
                            colour: tokens.brand500,
                            isMe: true,
                          ),
                      ],
                      onPinTap: (id) => setState(
                        () => _selected = widget.kiosks
                            .where((k) => k.id == id)
                            .firstOrNull,
                      ),
                      onMapTap: () => setState(() => _selected = null),
                      onLoaded: _loaded,
                    ),
                  ),
                ),
              if (_state == _MapState.loading)
                const Center(child: CircularProgressIndicator()),
              if (_selected case final kiosk?)
                Positioned(
                  left: Spacing.x4,
                  right: Spacing.x4,
                  bottom: Spacing.x8,
                  child: _SelectedCard(
                    kiosk: kiosk,
                    onOpen: () => widget.onOpen(kiosk),
                  ),
                ),
            ],
          ),
        ),
        if (settings != null) _Attribution(links: settings.attribution),
        const _Legend(),
      ],
    );
  }
}

/// The map data's credit, always under the map, each part a link to its
/// owner (OpenFreeMap's terms; T-286). Our own line rather than MapLibre's
/// folded "i", so it cannot be hidden.
class _Attribution extends ConsumerWidget {
  const _Attribution({required this.links});

  final List<MapAttributionLink> links;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final open = ref.read(browserProvider);
    final style = theme.textTheme.labelSmall?.copyWith(
      color: theme.colorScheme.primary,
      decoration: TextDecoration.underline,
    );

    return Padding(
      padding: const EdgeInsets.fromLTRB(Spacing.x4, Spacing.x1, Spacing.x4, 0),
      child: Align(
        alignment: Alignment.centerRight,
        child: Wrap(
          alignment: WrapAlignment.end,
          spacing: Spacing.x2,
          children: [
            for (final link in links)
              Semantics(
                link: true,
                child: InkWell(
                  onTap: () => unawaited(open(link.url)),
                  child: Padding(
                    padding: const EdgeInsets.symmetric(vertical: Spacing.x1),
                    child: Text(link.label, style: style),
                  ),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class _Legend extends StatelessWidget {
  const _Legend();

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;
    final l10n = AppLocalizations.of(context);

    return Padding(
      padding: const EdgeInsets.symmetric(
        horizontal: Spacing.x4,
        vertical: Spacing.x2,
      ),
      child: Semantics(
        label: l10n.kioskMapLegend,
        child: Wrap(
          spacing: Spacing.x4,
          runSpacing: Spacing.x1,
          children: [
            for (final status in KioskStatus.values)
              Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(
                    status.icon,
                    size: Spacing.x4,
                    color: tokens.forTone(status.tone).foreground,
                  ),
                  const SizedBox(width: Spacing.x1),
                  Text(
                    status.label(l10n),
                    style: Theme.of(context).textTheme.bodySmall,
                  ),
                ],
              ),
          ],
        ),
      ),
    );
  }
}

/// No map: why, a retry when there is one, and the same kiosks as a list.
class _Fallback extends StatelessWidget {
  const _Fallback({
    required this.kiosks,
    required this.onOpen,
    required this.title,
    required this.body,
    this.onRetry,
  });

  final List<KioskSummary> kiosks;
  final ValueChanged<KioskSummary> onOpen;
  final String title;
  final String body;
  final VoidCallback? onRetry;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context);
    final muted = theme.colorScheme.onSurfaceVariant;

    return ListView(
      padding: const EdgeInsets.fromLTRB(
        Spacing.x4,
        Spacing.x2,
        Spacing.x4,
        Spacing.x6,
      ),
      children: [
        Semantics(
          container: true,
          liveRegion: true,
          child: Column(
            children: [
              Icon(Icons.map_outlined, color: muted),
              const SizedBox(height: Spacing.x2),
              Text(
                title,
                textAlign: TextAlign.center,
                style: theme.textTheme.titleMedium,
              ),
              const SizedBox(height: Spacing.x1),
              Text(
                body,
                textAlign: TextAlign.center,
                style: theme.textTheme.bodyMedium?.copyWith(color: muted),
              ),
              if (onRetry case final retry?) ...[
                const SizedBox(height: Spacing.x3),
                OutlinedButton(onPressed: retry, child: Text(l10n.commonRetry)),
              ],
            ],
          ),
        ),
        const SizedBox(height: Spacing.x4),
        for (final kiosk in kiosks) ...[
          KioskCard(kiosk: kiosk, onTap: () => onOpen(kiosk)),
          const SizedBox(height: Spacing.x2),
        ],
      ],
    );
  }
}

class _SelectedCard extends StatelessWidget {
  const _SelectedCard({required this.kiosk, required this.onOpen});

  final KioskSummary kiosk;
  final VoidCallback onOpen;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context);

    return Card(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        // One column, the button full width under the text (the theme gives
        // buttons the full width; beside the text it would squeeze it).
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(kiosk.name, style: theme.textTheme.titleMedium),
            const SizedBox(height: Spacing.x1),
            Text(kiosk.code, style: theme.textTheme.bodySmall),
            const SizedBox(height: Spacing.x2),
            Wrap(
              spacing: Spacing.x2,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                KioskStatusChip(status: kiosk.status),
                if (kiosk.distanceKm case final km?)
                  Text(
                    distanceText(context, km),
                    style: theme.textTheme.bodySmall,
                  ),
              ],
            ),
            const SizedBox(height: Spacing.x3),
            OutlinedButton(onPressed: onOpen, child: Text(l10n.kioskMore)),
          ],
        ),
      ),
    );
  }
}
