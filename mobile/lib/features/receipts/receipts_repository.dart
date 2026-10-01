import '../../core/network/api_client.dart';
import '../../core/network/idempotency.dart';

/// One of the user's receipts (plan section 2.12). Its id is the payment's.
final class Receipt {
  const Receipt({
    required this.id,
    required this.number,
    required this.amount,
    required this.purpose,
    required this.fiscalStatus,
    required this.createdAt,
  });

  factory Receipt.fromJson(Map<String, dynamic> json) => Receipt(
    id: json['id'] as String,
    number: json['number'] as String,
    amount: (json['amount'] as num).toDouble(),
    purpose: json['purpose'] as String,
    fiscalStatus: json['fiscalStatus'] as String,
    createdAt: DateTime.parse(json['createdAt'] as String),
  );

  final String id;

  /// `NP-2026-000001`.
  final String number;
  final double amount;

  /// `PrintJob` or `WalletTopUp`.
  final String purpose;

  /// `Issued`, `Pending`, `Failed` or `None` (the fiscal receipt's state).
  final String fiscalStatus;
  final DateTime createdAt;
}

class ReceiptsRepository {
  ReceiptsRepository(this._api);

  final ApiClient _api;

  Future<List<Receipt>> list() => _api.send(
    method: 'GET',
    path: 'api/receipts',
    parse: (json) => (json as List<dynamic>)
        .map((item) => Receipt.fromJson(item as Map<String, dynamic>))
        .toList(),
  );

  /// To the account's confirmed address, with the PDF attached.
  Future<void> email(String receiptId, {required String idempotencyKey}) =>
      _api.send(
        method: 'POST',
        path: 'api/receipts/$receiptId/email',
        headers: {idempotencyKeyHeader: idempotencyKey},
        body: <String, dynamic>{},
        parse: (_) {},
      );
}
