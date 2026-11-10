import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/realtime/user_hub_provider.dart';
import '../../shared/paging.dart';
import 'share_models.dart';
import 'share_repository.dart';

/// "Alınanlar", a page at a time (T-413): refreshed when a `FileShared`
/// notification arrives, so the badge on "Fayllarım" and the open list show
/// it without a pull.
final receivedSharesProvider =
    AsyncNotifierProvider.autoDispose<
      ReceivedSharesController,
      Paged<ShareItem>
    >(ReceivedSharesController.new);

/// "Göndərilənlər", a page at a time (T-413).
final sentSharesProvider =
    AsyncNotifierProvider.autoDispose<SentSharesController, Paged<ShareItem>>(
      SentSharesController.new,
    );

/// One of the two share lists.
abstract class SharesController
    extends AutoDisposeAsyncNotifier<Paged<ShareItem>> {
  late PageLoader<ShareItem> _loader;

  /// A page of this list.
  Future<List<ShareItem>> fetch(ShareRepository repository, int page, int size);

  @override
  Future<Paged<ShareItem>> build() {
    ref.watch(currentUserProvider.select((user) => user?.id));

    final repository = ref.watch(shareRepositoryProvider);
    _loader = PageLoader(
      (page, size) => fetch(repository, page, size),
      (share) => share.id,
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

class ReceivedSharesController extends SharesController {
  @override
  Future<Paged<ShareItem>> build() {
    final news = ref
        .watch(userHubEventsProvider)
        .where(
          (event) =>
              event.target == 'Notification' &&
              event.payload['type'] == 'FileShared',
        )
        .listen((_) => unawaited(refresh()));
    ref.onDispose(() => unawaited(news.cancel()));

    return super.build();
  }

  @override
  Future<List<ShareItem>> fetch(
    ShareRepository repository,
    int page,
    int size,
  ) => repository.received(page: page, pageSize: size);
}

class SentSharesController extends SharesController {
  @override
  Future<List<ShareItem>> fetch(
    ShareRepository repository,
    int page,
    int size,
  ) => repository.sent(page: page, pageSize: size);
}

/// How many received files wait to be looked at: the badge on "Fayllarım".
final newSharesCountProvider = Provider.autoDispose<int>(
  (ref) =>
      ref
          .watch(receivedSharesProvider)
          .valueOrNull
          ?.items
          .where((share) => share.availability == ShareAvailability.available)
          .length ??
      0,
);
