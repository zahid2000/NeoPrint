import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/network/api_client.dart';
import '../../core/realtime/user_hub_provider.dart';
import 'wallet_repository.dart';

final walletRepositoryProvider = Provider<WalletRepository>(
  (ref) => WalletRepository(ref.watch(apiClientProvider)),
);

final walletProvider = AsyncNotifierProvider<WalletController, WalletState>(
  WalletController.new,
);

final class WalletState {
  const WalletState({required this.wallet, required this.entries});

  final Wallet wallet;
  final List<WalletEntry> entries;
}

/// The balance and its ledger (plan section 2.14). Read again when a payment
/// or a refund moves money, so the home card never shows a stale balance.
class WalletController extends AsyncNotifier<WalletState> {
  static const _triggers = {'PaymentStatusChanged', 'RefundStatusChanged'};

  WalletRepository get _repository => ref.read(walletRepositoryProvider);

  @override
  Future<WalletState> build() {
    final events = ref
        .watch(userHubEventsProvider)
        .where((event) => _triggers.contains(event.target))
        .listen((_) => unawaited(refresh()));
    final reconnected = ref
        .watch(userHubReconnectedProvider)
        .listen((_) => unawaited(refresh()));

    ref.onDispose(() {
      unawaited(events.cancel());
      unawaited(reconnected.cancel());
    });

    return _load();
  }

  Future<WalletState> _load() async {
    final results = await Future.wait([
      _repository.get(),
      _repository.transactions(),
    ]);

    return WalletState(
      wallet: results[0] as Wallet,
      entries: results[1] as List<WalletEntry>,
    );
  }

  /// Reads it again; a failure keeps what is on screen.
  Future<void> refresh() async {
    try {
      state = AsyncData(await _load());
    } on ApiException catch (error, stack) {
      if (!state.hasValue) {
        state = AsyncError(error, stack);
      }
    }
  }

  /// Adds money from a card. Returns why it was refused, or null.
  Future<ApiException?> topUp(
    double amount, {
    required String idempotencyKey,
  }) => _act(() => _repository.topUp(amount, idempotencyKey: idempotencyKey));

  Future<ApiException?> redeemVoucher(
    String code, {
    required String idempotencyKey,
  }) => _act(
    () => _repository.redeemVoucher(code, idempotencyKey: idempotencyKey),
  );

  Future<ApiException?> _act(Future<Wallet> Function() action) async {
    try {
      await action();
      await refresh();
      return null;
    } on ApiException catch (error) {
      return error;
    }
  }
}
