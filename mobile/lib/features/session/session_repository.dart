import '../../core/network/api_client.dart';
import '../../core/network/idempotency.dart';
import 'session_models.dart';

/// Sessions and the orders placed in them (docs/API.md "Sessiyalar").
class SessionRepository {
  SessionRepository(this._api);

  final ApiClient _api;

  /// Opens a session at the kiosk with this code or id; the same kiosk again
  /// extends it.
  Future<KioskSessionInfo> open(String kiosk) => _api.send(
    method: 'POST',
    path: 'api/sessions',
    body: {'kiosk': kiosk},
    parse: (json) => KioskSessionInfo.fromJson(json as Map<String, dynamic>),
  );

  /// The user's open session, or null.
  Future<KioskSessionInfo?> current() => _api.send(
    method: 'GET',
    path: 'api/sessions/current',
    parse: (json) =>
        json is Map<String, dynamic> ? KioskSessionInfo.fromJson(json) : null,
  );

  /// Ends the session. One that is already over is not found (404) - the
  /// server says the same of a session that is somebody else's (T-312) - and
  /// for the app that simply means there is nothing left to end.
  Future<void> close(String sessionId) async {
    try {
      await _api.send(
        method: 'DELETE',
        path: 'api/sessions/$sessionId',
        parse: (_) {},
      );
    } on ApiException catch (error) {
      if (error.statusCode != 404) {
        rethrow;
      }
    }
  }

  /// The price of these options for [pages] selected pages at the kiosk.
  Future<OrderQuote> quote({
    required String kioskId,
    required OrderOptions options,
    required int pages,
  }) => _api.send(
    method: 'POST',
    path: 'api/pricing/quote',
    body: {
      'kioskId': kioskId,
      'options': {
        'pageCount': pages,
        'copies': options.copies,
        'duplex': options.duplex,
        'color': options.color,
        'paper': options.paper,
        'pagesPerSheet': options.pagesPerSheet,
      },
      'useBonus': false,
    },
    parse: (json) => OrderQuote.fromJson(json as Map<String, dynamic>),
  );

  /// Places the order; a retry with the same [idempotencyKey] is the same order.
  Future<PlacedOrder> placeOrder({
    required String sessionId,
    required String fileId,
    required OrderOptions options,
    required String idempotencyKey,
  }) => _api.send(
    method: 'POST',
    path: 'api/sessions/$sessionId/print-jobs',
    headers: {idempotencyKeyHeader: idempotencyKey},
    body: {'fileId': fileId, 'options': options.toJson()},
    parse: (json) => PlacedOrder.fromJson(json as Map<String, dynamic>),
  );
}
