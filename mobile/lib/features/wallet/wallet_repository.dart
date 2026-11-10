import '../../core/network/api_client.dart';
import '../../core/network/idempotency.dart';

/// The wallet as the ledger has it (plan section 2.14).
final class Wallet {
  const Wallet({required this.balance, required this.bonusPages});

  factory Wallet.fromJson(Map<String, dynamic> json) => Wallet(
    balance: (json['balance'] as num).toDouble(),
    bonusPages: json['bonusPages'] as int? ?? 0,
  );

  final double balance;
  final int bonusPages;
}

/// One line of the ledger, newest first.
final class WalletEntry {
  const WalletEntry({
    required this.id,
    required this.type,
    required this.amount,
    required this.bonusPages,
    required this.balanceAfter,
    required this.createdAt,
    this.note,
  });

  factory WalletEntry.fromJson(Map<String, dynamic> json) => WalletEntry(
    id: json['id'] as String,
    type: json['type'] as String,
    amount: (json['amount'] as num).toDouble(),
    bonusPages: json['bonusPages'] as int? ?? 0,
    balanceAfter: (json['balanceAfter'] as num).toDouble(),
    createdAt: DateTime.parse(json['createdAt'] as String),
    note: json['note'] as String?,
  );

  final String id;

  /// `TopUp`, `Payment`, `Refund`, `Bonus`, `VoucherRedeem`, `AdminAdjust`.
  final String type;

  /// Signed: money in is positive.
  final double amount;
  final int bonusPages;
  final double balanceAfter;
  final DateTime createdAt;
  final String? note;
}

/// The wallet endpoints. There is no way to take money out: the server has
/// no such endpoint, and this class has no such method (plan section 2.14).
class WalletRepository {
  WalletRepository(this._api);

  final ApiClient _api;

  Future<Wallet> get() => _api.send(
    method: 'GET',
    path: 'api/wallet',
    parse: (json) => Wallet.fromJson(json as Map<String, dynamic>),
  );

  /// The newest movements, a [page] at a time (T-413); with [from]
  /// (inclusive) and [to] (exclusive) only those in that period (T-397).
  Future<List<WalletEntry>> transactions({
    int page = 1,
    int pageSize = 50,
    DateTime? from,
    DateTime? to,
  }) => _api.send(
    method: 'GET',
    path: 'api/wallet/transactions',
    query: {
      'page': page,
      'pageSize': pageSize,
      if (from != null) 'from': from.toUtc().toIso8601String(),
      if (to != null) 'to': to.toUtc().toIso8601String(),
    },
    parse: (json) => ((json as Map<String, dynamic>)['items'] as List<dynamic>)
        .map((item) => WalletEntry.fromJson(item as Map<String, dynamic>))
        .toList(),
  );

  /// Adds money from a card. The key stays the same for one tap's retries.
  Future<Wallet> topUp(double amount, {required String idempotencyKey}) =>
      _api.send(
        method: 'POST',
        path: 'api/wallet/top-up',
        headers: {idempotencyKeyHeader: idempotencyKey},
        body: {'amount': amount},
        parse: (json) => Wallet.fromJson(
          (json as Map<String, dynamic>)['wallet'] as Map<String, dynamic>,
        ),
      );

  /// A refund voucher (`RF-XXXX-XXXX`) into the wallet, once.
  Future<Wallet> redeemVoucher(String code, {required String idempotencyKey}) =>
      _api.send(
        method: 'POST',
        path: 'api/wallet/vouchers/redeem',
        headers: {idempotencyKeyHeader: idempotencyKey},
        body: {'code': code},
        parse: (json) => Wallet.fromJson(json as Map<String, dynamic>),
      );
}
