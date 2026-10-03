import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/network/api_client.dart';
import '../../core/realtime/user_hub_provider.dart';
import 'notification_models.dart';

/// The notifications list (docs/API.md "Bildirişlər").
class NotificationRepository {
  NotificationRepository(this._api);

  final ApiClient _api;

  Future<NotificationPage> list({DateTime? before, int take = 20}) => _api.send(
    method: 'GET',
    path: 'api/notifications',
    query: {
      'take': take,
      if (before != null) 'before': before.toUtc().toIso8601String(),
    },
    parse: (json) => NotificationPage.fromJson(json as Map<String, dynamic>),
  );

  Future<void> markRead(String id) => _api.send(
    method: 'POST',
    path: 'api/notifications/$id/read',
    parse: (_) {},
  );
}

final notificationRepositoryProvider = Provider<NotificationRepository>(
  (ref) => NotificationRepository(ref.watch(apiClientProvider)),
);

/// What the list shows: the pages read so far, the unread count, and whether
/// there is more.
class NotificationsState {
  const NotificationsState({
    required this.items,
    required this.unread,
    required this.next,
    this.loadingMore = false,
  });

  final List<AppNotification> items;
  final int unread;
  final DateTime? next;
  final bool loadingMore;

  bool get hasMore => next != null;

  NotificationsState copyWith({
    List<AppNotification>? items,
    int? unread,
    DateTime? next,
    bool clearNext = false,
    bool? loadingMore,
  }) => NotificationsState(
    items: items ?? this.items,
    unread: unread ?? this.unread,
    next: clearNext ? null : next ?? this.next,
    loadingMore: loadingMore ?? this.loadingMore,
  );
}

/// The user's notifications, newest first; read again when the hub says a
/// new one was stored, and after a reconnect.
final notificationsProvider =
    AsyncNotifierProvider<NotificationsController, NotificationsState>(
      NotificationsController.new,
    );

class NotificationsController extends AsyncNotifier<NotificationsState> {
  NotificationRepository get _repository =>
      ref.read(notificationRepositoryProvider);

  @override
  Future<NotificationsState> build() async {
    // A different account starts from its own list.
    ref.watch(currentUserProvider.select((user) => user?.id));

    final news = ref
        .watch(userHubEventsProvider)
        .where((event) => event.target == 'Notification')
        .listen((_) => unawaited(refresh()));
    final back = ref
        .watch(userHubReconnectedProvider)
        .listen((_) => unawaited(refresh()));

    ref.onDispose(() {
      unawaited(news.cancel());
      unawaited(back.cancel());
    });

    return _first();
  }

  Future<NotificationsState> _first() async {
    final page = await _repository.list();
    return NotificationsState(
      items: page.items,
      unread: page.unread,
      next: page.next,
    );
  }

  Future<void> refresh() async {
    state = await AsyncValue.guard(_first);
  }

  /// The next, older page; a second call while one loads does nothing.
  Future<void> loadMore() async {
    final current = state.valueOrNull;
    if (current == null || !current.hasMore || current.loadingMore) {
      return;
    }

    state = AsyncData(current.copyWith(loadingMore: true));
    try {
      final page = await _repository.list(before: current.next);
      state = AsyncData(
        current.copyWith(
          items: [...current.items, ...page.items],
          unread: page.unread,
          next: page.next,
          clearNext: page.next == null,
          loadingMore: false,
        ),
      );
    } on Object {
      state = AsyncData(current.copyWith(loadingMore: false));
      rethrow;
    }
  }

  /// Opened: read at once in the list and the badge, then on the server.
  Future<void> markRead(String id) async {
    final current = state.valueOrNull;
    final item = current?.items.where((n) => n.id == id).firstOrNull;
    if (current != null && item != null && !item.isRead) {
      state = AsyncData(
        current.copyWith(
          items: [
            for (final n in current.items)
              n.id == id ? n.read(DateTime.now()) : n,
          ],
          unread: current.unread > 0 ? current.unread - 1 : 0,
        ),
      );
    }

    await _repository.markRead(id);
  }
}

/// The badge on the bell: 0 while the list is still loading or failed.
final unreadNotificationsProvider = Provider<int>(
  (ref) => ref.watch(notificationsProvider).valueOrNull?.unread ?? 0,
);
