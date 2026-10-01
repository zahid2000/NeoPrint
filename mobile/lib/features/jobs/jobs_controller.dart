import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/network/api_client.dart';
import '../../core/realtime/user_hub_provider.dart';
import 'jobs_repository.dart';

final jobsRepositoryProvider = Provider<JobsRepository>(
  (ref) => JobsRepository(ref.watch(apiClientProvider)),
);

final myJobsProvider = AsyncNotifierProvider<MyJobsController, List<MyJob>>(
  MyJobsController.new,
);

final myClaimsProvider = FutureProvider.autoDispose<List<MyClaim>>(
  (ref) => ref.watch(jobsRepositoryProvider).claims(),
);

/// The user's jobs, kept current by the user hub: a status change, a payment
/// or a refund reads the list again, and so does a reconnection, which may
/// have missed some (plan section 6, `/hubs/user`).
class MyJobsController extends AsyncNotifier<List<MyJob>> {
  static const _triggers = {
    'JobStatusChanged',
    'PaymentStatusChanged',
    'RefundStatusChanged',
  };

  @override
  Future<List<MyJob>> build() {
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

    return ref.read(jobsRepositoryProvider).mine();
  }

  /// Reads the list again; a failure keeps what is on screen.
  Future<void> refresh() async {
    try {
      state = AsyncData(await ref.read(jobsRepositoryProvider).mine());
    } on ApiException catch (error, stack) {
      if (!state.hasValue) {
        state = AsyncError(error, stack);
      }
    }
  }

  /// "Problem barədə bildir". Returns why it was refused, or null.
  Future<ApiException?> report(
    String jobId,
    String text, {
    required String idempotencyKey,
  }) async {
    try {
      await ref
          .read(jobsRepositoryProvider)
          .report(jobId, text, idempotencyKey: idempotencyKey);
      ref.invalidate(myClaimsProvider);
      return null;
    } on ApiException catch (error) {
      return error;
    }
  }
}
