import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/network/api_client.dart';
import '../../core/realtime/user_hub_provider.dart';
import 'receipts_repository.dart';

final receiptsRepositoryProvider = Provider<ReceiptsRepository>(
  (ref) => ReceiptsRepository(ref.watch(apiClientProvider)),
);

/// The user's receipts. A new payment adds one, so the list is read again
/// when the hub says a payment went through.
final receiptsProvider = FutureProvider.autoDispose<List<Receipt>>((ref) {
  final events = ref
      .watch(userHubEventsProvider)
      .where((event) => event.target == 'PaymentStatusChanged')
      .listen((_) => ref.invalidateSelf());
  ref.onDispose(() => unawaited(events.cancel()));

  return ref.watch(receiptsRepositoryProvider).list();
});

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
