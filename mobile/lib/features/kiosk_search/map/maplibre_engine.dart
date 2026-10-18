import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:maplibre_gl/maplibre_gl.dart';

import '../../../core/theme/tokens.dart';
import 'kiosk_map_engine.dart';

/// MapLibre Native behind [KioskMapEngine] (T-217).
final class MapLibreEngine implements KioskMapEngine {
  const MapLibreEngine();

  @override
  bool get available => true;

  @override
  Widget build(BuildContext context, MapRequest request) =>
      _MapLibreKioskMap(request: request);
}

class _MapLibreKioskMap extends StatefulWidget {
  const _MapLibreKioskMap({required this.request});

  final MapRequest request;

  @override
  State<_MapLibreKioskMap> createState() => _MapLibreKioskMapState();
}

class _MapLibreKioskMapState extends State<_MapLibreKioskMap> {
  /// Baku: where the service starts.
  static const _baku = LatLng(40.4093, 49.8671);

  /// Close enough to read the streets around a single kiosk.
  static const _closestZoom = 16.0;

  MapLibreMapController? _controller;
  bool _styleLoaded = false;

  @override
  void didUpdateWidget(_MapLibreKioskMap old) {
    super.didUpdateWidget(old);
    if (_styleLoaded && !identical(old.request.pins, widget.request.pins)) {
      unawaited(_draw(fit: _idsOf(old.request) != _idsOf(widget.request)));
    }
  }

  static String _idsOf(MapRequest request) =>
      [for (final pin in request.pins) pin.id].join();

  @override
  Widget build(BuildContext context) => MapLibreMap(
    styleString: widget.request.styleUrl,
    initialCameraPosition: const CameraPosition(target: _baku, zoom: 11),
    // The attribution is the screen's own line under the map, always shown,
    // every part a link; the native "i" button would be a second, folded one.
    attributionButtonMargins: const math.Point(-1000, -1000),
    rotateGesturesEnabled: false,
    tiltGesturesEnabled: false,
    compassEnabled: false,
    onMapCreated: (controller) {
      _controller = controller;
      controller.onCircleTapped.add(_tapped);
    },
    onStyleLoadedCallback: () async {
      _styleLoaded = true;
      await _draw(fit: true);
      widget.request.onLoaded();
    },
    onMapClick: (_, __) => widget.request.onMapTap(),
  );

  void _tapped(Circle circle) {
    final id = circle.data?['id'] as String?;
    if (id != null && id.isNotEmpty) {
      widget.request.onPinTap(id);
    }
  }

  Future<void> _draw({required bool fit}) async {
    final controller = _controller;
    if (controller == null || !mounted) {
      return;
    }

    final tokens = context.tokens;
    await controller.clearCircles();

    for (final pin in widget.request.pins) {
      await controller.addCircle(
        CircleOptions(
          geometry: LatLng(pin.lat, pin.lng),
          circleRadius: pin.isMe
              ? Spacing.x2
              : pin.selected
              ? Spacing.x4
              : Spacing.x3,
          circleColor: _hex(pin.colour),
          circleStrokeColor: _hex(tokens.neutral0),
          circleStrokeWidth: pin.isMe ? 3 : 2,
        ),
        {'id': pin.isMe ? '' : pin.id},
      );
    }

    if (fit) {
      await _fit(controller);
    }
  }

  Future<void> _fit(MapLibreMapController controller) async {
    final pins = widget.request.pins;
    if (pins.isEmpty) {
      return;
    }

    if (pins.length == 1) {
      await controller.moveCamera(
        CameraUpdate.newLatLngZoom(
          LatLng(pins.single.lat, pins.single.lng),
          _closestZoom,
        ),
      );
      return;
    }

    final lats = pins.map((pin) => pin.lat);
    final lngs = pins.map((pin) => pin.lng);
    await controller.moveCamera(
      CameraUpdate.newLatLngBounds(
        LatLngBounds(
          southwest: LatLng(lats.reduce(math.min), lngs.reduce(math.min)),
          northeast: LatLng(lats.reduce(math.max), lngs.reduce(math.max)),
        ),
        left: Spacing.x12,
        top: Spacing.x12,
        right: Spacing.x12,
        bottom: Spacing.x12,
      ),
    );
  }

  /// MapLibre takes colours as text; these come from the design tokens.
  static String _hex(Color colour) {
    int channel(double value) => (value * 255).round() & 0xff;
    return '#${[channel(colour.r), channel(colour.g), channel(colour.b)].map((value) => value.toRadixString(16).padLeft(2, '0')).join()}';
  }
}
