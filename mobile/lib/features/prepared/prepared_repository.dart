import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_client.dart';
import '../../core/network/idempotency.dart';
import '../../core/auth/auth_controller.dart';
import '../session/session_models.dart';
import 'prepared_models.dart';

/// Preparations for a kiosk and the personal QR (docs/API.md "Şəxsi QR və
/// Kiosk üçün hazırla").
class PreparedRepository {
  PreparedRepository(this._api);

  final ApiClient _api;

  Future<List<PreparedPrint>> list() => _api.send(
    method: 'GET',
    path: 'api/prepared-prints',
    parse: (json) => [
      for (final item
          in (json as Map<String, dynamic>)['items'] as List<dynamic>)
        PreparedPrint.fromJson(item as Map<String, dynamic>),
    ],
  );

  /// Unpaid, for any kiosk: the file and what was chosen so far.
  Future<PreparedPrint> prepare({
    required String fileId,
    required PreparedPreset preset,
    required String idempotencyKey,
  }) => _api.send(
    method: 'POST',
    path: 'api/prepared-prints',
    headers: {idempotencyKeyHeader: idempotencyKey},
    body: {'fileId': fileId, 'options': preset.toJson()},
    parse: (json) => PreparedPrint.fromJson(json as Map<String, dynamic>),
  );

  Future<void> remove(String id) => _api.send(
    method: 'DELETE',
    path: 'api/prepared-prints/$id',
    parse: (_) {},
  );

  /// Paid in advance at [kiosk]: the order and the payment that pays it.
  /// [expectedAmount] is the price shown; the server refuses with
  /// `printJobs.priceChanged` when the kiosk's prices changed since.
  Future<PlacedOrder> preparePaid({
    required String kiosk,
    required String fileId,
    required OrderOptions options,
    required double expectedAmount,
    required String idempotencyKey,
  }) => _api.send(
    method: 'POST',
    path: 'api/prepared-prints/paid',
    headers: {idempotencyKeyHeader: idempotencyKey},
    body: {
      'kiosk': kiosk,
      'fileId': fileId,
      'options': options.toJson(),
      'expectedAmount': expectedAmount,
    },
    parse: (json) => PlacedOrder.fromJson(json as Map<String, dynamic>),
  );

  /// Cancels an order paid in advance; the money goes back to the wallet.
  Future<void> cancelPaid(String jobId) => _api.send(
    method: 'POST',
    path: 'api/prepared-prints/paid/$jobId/cancel',
    parse: (_) {},
  );

  /// The personal QR as a picture, with when it stops being good.
  Future<MyQr> qr() async {
    DateTime? expiresAt;
    var rotation = 60;

    final png = await _api.send<Uint8List>(
      method: 'GET',
      path: 'api/users/me/qr.png',
      responseType: ResponseType.bytes,
      response: (reply) {
        final expires = reply.headers.value('x-qr-expires-at');
        expiresAt = expires == null ? null : DateTime.tryParse(expires);
        rotation =
            int.tryParse(reply.headers.value('x-qr-rotation-seconds') ?? '') ??
            rotation;
      },
      parse: (data) => Uint8List.fromList(data as List<int>),
    );

    return MyQr(
      png: png,
      // Without the header, the shortest life the server gives a code.
      expiresAt: expiresAt ?? DateTime.now().add(const Duration(seconds: 15)),
      rotationSeconds: rotation,
    );
  }
}

final preparedRepositoryProvider = Provider<PreparedRepository>(
  (ref) => PreparedRepository(ref.watch(apiClientProvider)),
);

/// What the user got ready for kiosks, newest first.
final preparedPrintsProvider =
    AsyncNotifierProvider<PreparedPrintsController, List<PreparedPrint>>(
      PreparedPrintsController.new,
    );

class PreparedPrintsController extends AsyncNotifier<List<PreparedPrint>> {
  PreparedRepository get _repository => ref.read(preparedRepositoryProvider);

  @override
  Future<List<PreparedPrint>> build() => _repository.list();

  Future<void> refresh() async {
    state = await AsyncValue.guard(_repository.list);
  }

  /// Takes an unpaid one off the list.
  Future<void> remove(String id) async {
    await _repository.remove(id);
    await refresh();
  }

  /// Cancels a paid one; its money goes back to the wallet.
  Future<void> cancelPaid(String jobId) async {
    await _repository.cancelPaid(jobId);
    await refresh();
  }
}
