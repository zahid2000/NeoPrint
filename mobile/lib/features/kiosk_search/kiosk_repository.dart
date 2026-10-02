import '../../core/network/api_client.dart';
import 'kiosk_models.dart';

/// Where the user is, in degrees.
typedef GeoPoint = ({double lat, double lng});

/// The kiosk search and detail (docs/API.md "Kiosk axtarışı").
class KioskRepository {
  KioskRepository(this._api);

  final ApiClient _api;

  /// By part of the name, code or address; nearest first when [near] is
  /// given, no farther than [radiusKm].
  Future<List<KioskSummary>> search({
    String? text,
    GeoPoint? near,
    double? radiusKm,
  }) => _api.send(
    method: 'GET',
    path: 'api/kiosks',
    query: {
      if (text != null && text.trim().isNotEmpty) 'search': text.trim(),
      if (near != null) ...{'lat': near.lat, 'lng': near.lng},
      if (near != null && radiusKm != null) 'radiusKm': radiusKm,
    },
    parse: (json) => [
      for (final item in (json as Map<String, dynamic>)['items'] as List)
        KioskSummary.fromJson(item as Map<String, dynamic>),
    ],
  );

  /// By id or code (`KSK-0001`, any case).
  Future<KioskDetail> detail(String idOrCode) => _api.send(
    method: 'GET',
    path: 'api/kiosks/${Uri.encodeComponent(idOrCode)}',
    parse: (json) => KioskDetail.fromJson(json as Map<String, dynamic>),
  );
}
