import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/realtime/user_hub_provider.dart';
import 'share_models.dart';
import 'share_repository.dart';

/// "Alınanlar": refreshed when a `FileShared` notification arrives, so the
/// badge on "Fayllarım" and the open list show it without a pull.
final receivedSharesProvider = FutureProvider.autoDispose<List<ShareItem>>((
  ref,
) {
  ref.watch(currentUserProvider.select((user) => user?.id));

  final news = ref
      .watch(userHubEventsProvider)
      .where(
        (event) =>
            event.target == 'Notification' &&
            event.payload['type'] == 'FileShared',
      )
      .listen((_) => ref.invalidateSelf());
  ref.onDispose(() => unawaited(news.cancel()));

  return ref.watch(shareRepositoryProvider).received();
});

/// "Göndərilənlər".
final sentSharesProvider = FutureProvider.autoDispose<List<ShareItem>>((ref) {
  ref.watch(currentUserProvider.select((user) => user?.id));

  return ref.watch(shareRepositoryProvider).sent();
});

/// How many received files wait to be looked at: the badge on "Fayllarım".
final newSharesCountProvider = Provider.autoDispose<int>(
  (ref) =>
      ref
          .watch(receivedSharesProvider)
          .valueOrNull
          ?.where((share) => share.availability == ShareAvailability.available)
          .length ??
      0,
);
