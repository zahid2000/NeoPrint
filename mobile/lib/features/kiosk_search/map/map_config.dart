import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/auth/auth_controller.dart';
import '../../../core/network/api_client.dart';

/// One link of the attribution line under the map.
final class MapAttributionLink {
  const MapAttributionLink({required this.label, required this.url});

  factory MapAttributionLink.fromJson(Map<String, dynamic> json) =>
      MapAttributionLink(
        label: json['label'] as String,
        url: Uri.parse(json['url'] as String),
      );

  final String label;
  final Uri url;
}

/// `GET api/map/config` (T-217): the style to draw and who to credit - the
/// server's settings, so an own tile server needs no new app.
final class MapConfig {
  const MapConfig({required this.styleUrl, required this.attribution});

  factory MapConfig.fromJson(Map<String, dynamic> json) => MapConfig(
    styleUrl: json['styleUrl'] as String,
    attribution: [
      for (final item in json['attribution'] as List)
        MapAttributionLink.fromJson(item as Map<String, dynamic>),
    ],
  );

  final String styleUrl;
  final List<MapAttributionLink> attribution;
}

/// Reads [MapConfig]; anonymous, as the map is there before anyone signs in.
class MapConfigRepository {
  MapConfigRepository(this._api);

  final ApiClient _api;

  Future<MapConfig> read() => _api.send(
    method: 'GET',
    path: 'api/map/config',
    authenticated: false,
    parse: (json) => MapConfig.fromJson(json as Map<String, dynamic>),
  );
}

final mapConfigRepositoryProvider = Provider<MapConfigRepository>(
  (ref) => MapConfigRepository(ref.watch(apiClientProvider)),
);

/// Asked again each time a map opens (T-286: "hər açılışda yenidən cəhd").
final mapConfigProvider = FutureProvider.autoDispose<MapConfig>(
  (ref) => ref.watch(mapConfigRepositoryProvider).read(),
);
