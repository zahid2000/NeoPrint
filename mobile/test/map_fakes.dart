// The kiosks map without a platform view (T-217): a widget test has no
// MapLibre, so the screen is tested on what it does with a map - the pins it
// asks for, the attribution, the fallback - and the real drawing is checked
// on the emulator.
import 'package:flutter/material.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/features/kiosk_search/map/kiosk_map_engine.dart';
import 'package:neoprint/features/kiosk_search/map/map_config.dart';

/// Draws each pin as a button with its label; loads at once unless told not to.
class FakeMapEngine implements KioskMapEngine {
  FakeMapEngine({this.available = true, this.loads = true});

  @override
  final bool available;

  /// False: the style never comes, as with a map server out of reach.
  bool loads;

  final requests = <MapRequest>[];

  @override
  Widget build(BuildContext context, MapRequest request) {
    requests.add(request);
    if (loads) {
      WidgetsBinding.instance.addPostFrameCallback((_) => request.onLoaded());
    }

    return Column(
      key: const Key('fake-map'),
      children: [
        Text('style: ${request.styleUrl}'),
        for (final pin in request.pins.where((pin) => !pin.isMe))
          TextButton(
            onPressed: () => request.onPinTap(pin.id),
            child: Text('pin: ${pin.label}'),
          ),
      ],
    );
  }
}

final openFreeMap = MapConfig(
  styleUrl: 'https://tiles.openfreemap.org/styles/liberty',
  attribution: [
    MapAttributionLink(label: 'OpenFreeMap', url: Uri.parse(_ofm)),
    MapAttributionLink(label: '© OpenMapTiles', url: Uri.parse(_omt)),
    MapAttributionLink(
      label: '© OpenStreetMap contributors',
      url: Uri.parse(_osm),
    ),
  ],
);

const _ofm = 'https://openfreemap.org';
const _omt = 'https://www.openmaptiles.org/';
const _osm = 'https://www.openstreetmap.org/copyright';

/// Answers with [config], or fails as an unreachable server would.
class FakeMapConfig implements MapConfigRepository {
  FakeMapConfig([MapConfig? config]) : config = config ?? openFreeMap;

  MapConfig config;
  bool fails = false;
  var asked = 0;

  @override
  Future<MapConfig> read() async {
    asked++;
    if (fails) {
      throw const ApiException.unreachable();
    }
    return config;
  }
}
