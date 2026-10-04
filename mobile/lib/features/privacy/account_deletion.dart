import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/network/api_client.dart';
import '../../core/network/idempotency.dart';

/// What keeps the account from being deleted, and what would be lost
/// (`GET api/users/me/delete`, docs/DECISIONS.md T-147).
class DeletionCheck {
  const DeletionCheck({
    required this.activePrints,
    required this.scheduledPrints,
    required this.uncollectedOrders,
    required this.pendingRefunds,
    required this.balance,
    required this.bonusPages,
  });

  factory DeletionCheck.fromJson(Map<String, dynamic> json) {
    final blockers = json['blockers'] as Map<String, dynamic>;
    int count(String key) => (blockers[key] as num?)?.toInt() ?? 0;

    return DeletionCheck(
      activePrints: count('activePrints'),
      scheduledPrints: count('scheduledPrints'),
      uncollectedOrders: count('uncollectedOrders'),
      pendingRefunds: count('pendingRefunds'),
      balance: (json['balance'] as num?)?.toDouble() ?? 0,
      bonusPages: (json['bonusPages'] as num?)?.toInt() ?? 0,
    );
  }

  final int activePrints;
  final int scheduledPrints;
  final int uncollectedOrders;
  final int pendingRefunds;

  /// Lost with the account (plan section 2.14).
  final double balance;
  final int bonusPages;

  bool get blocked =>
      activePrints + scheduledPrints + uncollectedOrders + pendingRefunds > 0;
}

class AccountDeletionRepository {
  AccountDeletionRepository(this._api);

  final ApiClient _api;

  Future<DeletionCheck> check() => _api.send(
    method: 'GET',
    path: 'api/users/me/delete',
    parse: (json) => DeletionCheck.fromJson(json as Map<String, dynamic>),
  );

  /// Closes the account; refused with `privacy.deleteBlocked`, a wrong
  /// password or a wrong confirmation word.
  Future<void> delete({
    required String password,
    required String confirmation,
  }) => _api.send<void>(
    method: 'POST',
    path: 'api/users/me/delete',
    headers: {idempotencyKeyHeader: newIdempotencyKey()},
    body: {'password': password, 'confirmation': confirmation},
    parse: (_) {},
  );
}

final accountDeletionRepositoryProvider = Provider<AccountDeletionRepository>(
  (ref) => AccountDeletionRepository(ref.watch(apiClientProvider)),
);

final deletionCheckProvider = FutureProvider.autoDispose<DeletionCheck>(
  (ref) => ref.watch(accountDeletionRepositoryProvider).check(),
);

/// Signs out after the deletion; a provider so a test can watch it happen.
final accountSignOutProvider = Provider<Future<void> Function()>(
  (ref) => ref.read(authControllerProvider.notifier).signOut,
);
