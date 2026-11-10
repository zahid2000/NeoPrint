import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/network/api_client.dart';
import '../../core/realtime/user_hub_provider.dart';
import '../../shared/paging.dart';
import 'jobs_repository.dart';

final jobsRepositoryProvider = Provider<JobsRepository>(
  (ref) => JobsRepository(ref.watch(apiClientProvider)),
);

final myJobsProvider = AsyncNotifierProvider<MyJobsController, Paged<MyJob>>(
  MyJobsController.new,
);

/// The user's "Problem barədə bildir" messages, a page at a time (T-413).
final myClaimsProvider =
    AsyncNotifierProvider.autoDispose<MyClaimsController, Paged<MyClaim>>(
      MyClaimsController.new,
    );

class MyClaimsController extends AutoDisposeAsyncNotifier<Paged<MyClaim>> {
  late PageLoader<MyClaim> _loader;

  @override
  Future<Paged<MyClaim>> build() {
    final repository = ref.watch(jobsRepositoryProvider);
    _loader = PageLoader(
      (page, size) => repository.claims(page: page, pageSize: size),
      (claim) => claim.id,
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

/// The user's jobs, newest first and a page at a time (T-413), kept current
/// by the user hub: a status change, a payment or a refund reads the list
/// again, and so does a reconnection, which may have missed some (plan
/// section 6, `/hubs/user`).
class MyJobsController extends AsyncNotifier<Paged<MyJob>> {
  static const _triggers = {
    'JobStatusChanged',
    'PaymentStatusChanged',
    'RefundStatusChanged',
    // "The kiosk stopped": the order's status has not changed, what it
    // offers has.
    'Notification',
  };

  late final _loader = PageLoader<MyJob>(
    (page, size) =>
        ref.read(jobsRepositoryProvider).mine(page: page, pageSize: size),
    (job) => job.id,
  );

  @override
  Future<Paged<MyJob>> build() {
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

    return _loader.first();
  }

  /// Reads the list again, as long as it is; a failure keeps what is on
  /// screen.
  Future<void> refresh() =>
      refreshPaged(_loader, () => state, (value) => state = value);

  /// The next, older page under the list.
  Future<void> loadMore({bool retry = false}) => loadMorePaged(
    _loader,
    () => state,
    (value) => state = value,
    retry: retry,
  );

  /// Cancels a waiting order and reads the list again. Returns why it was
  /// refused, or null.
  Future<ApiException?> cancel(String jobId) async {
    try {
      await ref.read(jobsRepositoryProvider).cancel(jobId);
      await refresh();
      return null;
    } on ApiException catch (error) {
      return error;
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
