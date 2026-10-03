import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/realtime/user_hub_provider.dart';
import 'session_models.dart';
import 'session_repository.dart';

final sessionRepositoryProvider = Provider<SessionRepository>(
  (ref) => SessionRepository(ref.watch(apiClientProvider)),
);

/// The user's open kiosk session, or null (plan section 9.3).
///
/// Read from the server when the app starts and whenever the hub comes back,
/// so a session opened on another phone of the same account, or ended while
/// the app was away, shows as it is. The hub's `SessionClosed` takes it off
/// at once; a session past its end goes even if that message is lost.
class SessionController extends AsyncNotifier<KioskSessionInfo?> {
  Timer? _end;

  SessionRepository get _repository => ref.read(sessionRepositoryProvider);

  @override
  Future<KioskSessionInfo?> build() async {
    ref.onDispose(() => _end?.cancel());

    final closed = ref
        .watch(userHubEventsProvider)
        .where((event) => event.target == 'SessionClosed')
        .listen((event) {
          if (state.valueOrNull?.id == event.payload['sessionId']) {
            _set(null);
          }
        });
    final reconnected = ref
        .watch(userHubReconnectedProvider)
        .listen((_) => unawaited(refresh()));
    ref.onDispose(() {
      unawaited(closed.cancel());
      unawaited(reconnected.cancel());
    });

    final session = await _repository.current();
    _watchEnd(session);
    return session;
  }

  /// Opens (or extends) a session at the kiosk with this code or id.
  Future<KioskSessionInfo> open(String kiosk) async {
    final session = await _repository.open(kiosk);
    _set(session);
    return session;
  }

  /// Ends the session. Already over is no error.
  Future<void> close() async {
    final session = state.valueOrNull;

    if (session == null) {
      return;
    }

    await _repository.close(session.id);
    _set(null);
  }

  /// Reads the session again: an order placed in it moved its end on.
  Future<void> refresh() async {
    try {
      _set(await _repository.current());
    } on Exception {
      // The banner keeps what it had; the next refresh puts it right.
    }
  }

  void _set(KioskSessionInfo? session) {
    _watchEnd(session);
    state = AsyncData(session);
  }

  /// At its end, the server is asked: an order may have extended it.
  void _watchEnd(KioskSessionInfo? session) {
    _end?.cancel();

    if (session == null) {
      return;
    }

    final left = session.expiresAt.difference(DateTime.now());
    _end = Timer(
      left.isNegative ? Duration.zero : left,
      () => unawaited(refresh()),
    );
  }
}

final sessionProvider =
    AsyncNotifierProvider<SessionController, KioskSessionInfo?>(
      SessionController.new,
    );
