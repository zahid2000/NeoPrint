import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../auth/auth_controller.dart';
import 'user_hub.dart';

/// The user hub while someone is signed in; null otherwise.
///
/// Opened when the user signs in and closed when they sign out, so a second
/// account on the same phone never hears the first one's messages.
final userHubProvider = Provider<UserHubConnection?>((ref) {
  final userId = ref.watch(currentUserProvider.select((user) => user?.id));
  if (userId == null) {
    return null;
  }

  final api = ref.watch(apiClientProvider);
  final hub = UserHubConnection(
    baseUrl: apiBaseUrl,
    accessToken: api.freshAccessToken,
    negotiate: () => api.send(
      method: 'POST',
      path: 'hubs/user/negotiate',
      query: {'negotiateVersion': 1},
      parse: (json) =>
          (json as Map<String, dynamic>)['connectionToken'] as String,
    ),
    connect: WebSocketHubSocket.open,
  )..start();

  ref.onDispose(() => unawaited(hub.dispose()));

  return hub;
});

/// Every message on the user hub, and an empty stream when signed out.
final userHubEventsProvider = Provider<Stream<HubEvent>>(
  (ref) => ref.watch(userHubProvider)?.events ?? const Stream.empty(),
);

/// Fires when the hub comes back after a drop: a screen reads again what it
/// may have missed meanwhile.
final userHubReconnectedProvider = Provider<Stream<void>>(
  (ref) => ref.watch(userHubProvider)?.reconnected ?? const Stream.empty(),
);
