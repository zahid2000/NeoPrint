import '../../core/network/api_client.dart';
import '../../core/network/idempotency.dart';
import 'payment_models.dart';

/// Paying a kiosk order with the app (plan section 6, "Payments").
class PaymentsRepository {
  PaymentsRepository(this._api);

  final ApiClient _api;

  Future<AppPayment> get(String paymentId) => _api.send(
    method: 'GET',
    path: 'api/payments/$paymentId/app',
    parse: (json) => AppPayment.fromJson(json as Map<String, dynamic>),
  );

  /// Pays the order. [expectedAmount] is the money part the screen showed:
  /// a different price is refused by the server, never charged silently.
  ///
  /// [idempotencyKey] stays the same for one tap's retries, so a lost answer
  /// cannot take the money twice.
  Future<AppPayment> pay(
    String paymentId, {
    required PaymentSource source,
    required bool useBonus,
    required double expectedAmount,
    required String idempotencyKey,
    String? promoCode,
  }) => _api.send(
    method: 'POST',
    path: 'api/payments/$paymentId/pay-with-app',
    headers: {idempotencyKeyHeader: idempotencyKey},
    body: {
      'source': source.wire,
      'useBonus': useBonus,
      'expectedAmount': expectedAmount,
      'promoCode': ?promoCode,
    },
    parse: (json) => AppPayment.fromJson(json as Map<String, dynamic>),
  );

  /// What a code typed in the app takes off [amount] (what the bonus pages
  /// leave). Nothing is held; the payment checks it again.
  Future<PromoPreview> previewPromo(String code, double amount) => _api.send(
    method: 'POST',
    path: 'api/promos/validate',
    body: {'code': code, 'amount': amount},
    parse: (json) => PromoPreview.fromJson(json as Map<String, dynamic>),
  );
}
