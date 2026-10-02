import 'package:flutter/foundation.dart';

/// Where the map's tiles come from (docs/TASKS.md T-061).
///
/// The public OpenStreetMap server is for development only: its usage policy
/// does not allow an app's users to load it in bulk. A release build takes
/// its server from `--dart-define=NEOPRINT_MAP_TILE_URL=...` (an own tile
/// server or a provider that allows it - decided in step 7); a release built
/// without one has no map at all, only the list, rather than a map that
/// breaks the policy or stays blank.
abstract final class MapTiles {
  static const _configured = String.fromEnvironment('NEOPRINT_MAP_TILE_URL');

  static const _attribution = String.fromEnvironment(
    'NEOPRINT_MAP_ATTRIBUTION',
    defaultValue: '© OpenStreetMap contributors',
  );

  static const _developmentServer =
      'https://tile.openstreetmap.org/{z}/{x}/{y}.png';

  /// The tile URL template, or null when this build has none.
  static String? get url => _configured.isNotEmpty
      ? _configured
      : kReleaseMode
      ? null
      : _developmentServer;

  /// Always shown on the map (OSM licence; docs/DESIGN_SYSTEM.md).
  static String get attribution => _attribution;

  /// Sent with every tile request, as the OSM policy asks: who is asking.
  static const userAgentPackageName = 'az.neoprint.neoprint';
}
