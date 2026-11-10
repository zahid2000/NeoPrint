import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/network/api_client.dart';
import '../../core/realtime/user_hub_provider.dart';
import '../../shared/paging.dart';
import 'receipts_repository.dart';

final receiptsRepositoryProvider = Provider<ReceiptsRepository>(
  (ref) => ReceiptsRepository(ref.watch(apiClientProvider)),
);

/// The user's receipts, a page at a time (T-413). A new payment adds one,
/// so the list is read again when the hub says a payment went through.
final receiptsProvider =
    AsyncNotifierProvider.autoDispose<ReceiptsController, Paged<Receipt>>(
      ReceiptsController.new,
    );

class ReceiptsController extends AutoDisposeAsyncNotifier<Paged<Receipt>> {
  late PageLoader<Receipt> _loader;

  @override
  Future<Paged<Receipt>> build() {
    final events = ref
        .watch(userHubEventsProvider)
        .where((event) => event.target == 'PaymentStatusChanged')
        .listen((_) => unawaited(refresh()));
    ref.onDispose(() => unawaited(events.cancel()));

    final repository = ref.watch(receiptsRepositoryProvider);
    _loader = PageLoader(
      (page, size) => repository.list(page: page, pageSize: size),
      (receipt) => receipt.id,
    );
    return _loader.first();
  }

  Future<void> refresh() =>
      refreshPaged(_loader, () => state, (value) => state = value);

  Future<void> loadMore({bool retry = false}) => loadMorePaged(
    _loader,
    () => state,
    (value) => state = value,
    retry: retry,
  );
}

/// Emails a receipt to the account's address. Returns why it was refused, or
/// null.
Future<ApiException?> emailReceipt(
  WidgetRef ref,
  String receiptId, {
  required String idempotencyKey,
}) async {
  try {
    await ref
        .read(receiptsRepositoryProvider)
        .email(receiptId, idempotencyKey: idempotencyKey);
    return null;
  } on ApiException catch (error) {
    return error;
  }
}
