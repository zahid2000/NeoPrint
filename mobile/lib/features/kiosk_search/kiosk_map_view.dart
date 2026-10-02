import 'package:flutter/material.dart';
import 'package:flutter_map/flutter_map.dart';
import 'package:latlong2/latlong.dart';

import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import 'kiosk_models.dart';
import 'kiosk_repository.dart';
import 'kiosk_status_view.dart';
import 'map_tiles.dart';

/// The search result on OpenStreetMap (docs/DESIGN_SYSTEM.md, "Mobil:
/// kiosklar"): a marker per kiosk in its status colour with its status icon,
/// a legend below, the attribution always in sight, and a card for the kiosk
/// tapped. An alternative to the list, never more or fewer kiosks than it.
class KioskMapView extends StatefulWidget {
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
  State<KioskMapView> createState() => _KioskMapViewState();
}

class _KioskMapViewState extends State<KioskMapView> {
  /// The largest a marker is drawn, also its touch target.
  static const _markerSize = Sizes.touchTarget;

  /// Close enough to read the streets around a single kiosk.
  static const _closestZoom = 16.0;

  KioskSummary? _selected;

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
    final tokens = context.tokens;
    final l10n = AppLocalizations.of(context);
    final points = [
      for (final kiosk in widget.kiosks) LatLng(kiosk.lat, kiosk.lng),
      if (widget.me case final me?) LatLng(me.lat, me.lng),
    ];

    return Column(
      children: [
        Expanded(
          child: Stack(
            children: [
              FlutterMap(
                // A new result frames itself again.
                key: ValueKey(
                  [for (final kiosk in widget.kiosks) kiosk.id].join(),
                ),
                options: MapOptions(
                  initialCameraFit: CameraFit.coordinates(
                    coordinates: points,
                    padding: const EdgeInsets.all(Spacing.x12),
                    maxZoom: _closestZoom,
                  ),
                  onTap: (_, __) => setState(() => _selected = null),
                ),
                children: [
                  TileLayer(
                    urlTemplate: MapTiles.url,
                    userAgentPackageName: MapTiles.userAgentPackageName,
                  ),
                  MarkerLayer(
                    markers: [
                      if (widget.me case final me?)
                        Marker(
                          point: LatLng(me.lat, me.lng),
                          width: Spacing.x6,
                          height: Spacing.x6,
                          child: _MeDot(colour: tokens.brand500),
                        ),
                      for (final kiosk in widget.kiosks)
                        Marker(
                          point: LatLng(kiosk.lat, kiosk.lng),
                          width: _markerSize,
                          height: _markerSize,
                          child: _KioskMarker(
                            kiosk: kiosk,
                            selected: _selected?.id == kiosk.id,
                            onTap: () => setState(() => _selected = kiosk),
                          ),
                        ),
                    ],
                  ),
                  const _Attribution(),
                ],
              ),
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
        Padding(
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
        ),
      ],
    );
  }
}

/// The map data's licence, always in sight in the corner (OSM licence;
/// docs/DESIGN_SYSTEM.md). Our own rather than flutter_map's, which puts its
/// own name and a second "©" in front.
class _Attribution extends StatelessWidget {
  const _Attribution();

  @override
  Widget build(BuildContext context) => Align(
    alignment: Alignment.bottomRight,
    child: ColoredBox(
      color: context.tokens.neutral0,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: Spacing.x1),
        child: Text(
          MapTiles.attribution,
          style: Theme.of(context).textTheme.labelSmall,
        ),
      ),
    ),
  );
}

class _KioskMarker extends StatelessWidget {
  const _KioskMarker({
    required this.kiosk,
    required this.selected,
    required this.onTap,
  });

  final KioskSummary kiosk;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;
    final l10n = AppLocalizations.of(context);
    final colour = tokens.forTone(kiosk.status.tone).foreground;

    return Semantics(
      button: true,
      label: '${kiosk.name}, ${kiosk.status.label(l10n)}',
      child: GestureDetector(
        onTap: onTap,
        child: Center(
          child: AnimatedContainer(
            duration: Motion.fast,
            width: selected ? Sizes.touchTarget : Spacing.x8,
            height: selected ? Sizes.touchTarget : Spacing.x8,
            decoration: BoxDecoration(
              color: colour,
              shape: BoxShape.circle,
              border: Border.all(color: tokens.neutral0, width: 2),
            ),
            child: Icon(
              kiosk.status.icon,
              size: Spacing.x4,
              color: tokens.neutral0,
            ),
          ),
        ),
      ),
    );
  }
}

class _MeDot extends StatelessWidget {
  const _MeDot({required this.colour});

  final Color colour;

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: BoxDecoration(
      color: colour,
      shape: BoxShape.circle,
      border: Border.all(color: context.tokens.neutral0, width: 3),
    ),
  );
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
