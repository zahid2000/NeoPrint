import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'maplibre_engine.dart';

/// A point drawn on the map: a kiosk, or where the user is.
final class MapPin {
  const MapPin({
    required this.id,
    required this.lat,
    required this.lng,
    required this.colour,
    this.label = '',
    this.selected = false,
    this.isMe = false,
  });

  final String id;
  final double lat;
  final double lng;

  /// A design token's colour: the kiosk's status tone, or the brand for "me".
  final Color colour;

  /// The kiosk's name and status, for a screen reader.
  final String label;
  final bool selected;
  final bool isMe;
}

/// What the map is asked to draw, and what it reports back.
final class MapRequest {
  const MapRequest({
    required this.styleUrl,
    required this.pins,
    required this.onPinTap,
    required this.onMapTap,
    required this.onLoaded,
  });

  final String styleUrl;
  final List<MapPin> pins;
  final ValueChanged<String> onPinTap;
  final VoidCallback onMapTap;

  /// The style has loaded and the pins are on it. A map that never calls it
  /// is given up on by the screen after its timeout.
  final VoidCallback onLoaded;
}

/// Draws the kiosks map (T-217). The app's is MapLibre Native; a widget test
/// has no platform view, so it gives a fake one and checks what the screen
/// does with it - pins, the attribution, the fallback.
abstract interface class KioskMapEngine {
  /// False where this build cannot draw a map at all (the web build: the
  /// plugin would load its library from a CDN - T-286).
  bool get available;

  Widget build(BuildContext context, MapRequest request);
}

final kioskMapEngineProvider = Provider<KioskMapEngine>(
  (ref) => kIsWeb ? const NoMapEngine() : const MapLibreEngine(),
);

/// The web build: no map, the list and a word of why.
final class NoMapEngine implements KioskMapEngine {
  const NoMapEngine();

  @override
  bool get available => false;

  @override
  Widget build(BuildContext context, MapRequest request) =>
      const SizedBox.shrink();
}
