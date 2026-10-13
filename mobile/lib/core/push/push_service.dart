import 'dart:async';

import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../firebase_options.dart';
import '../auth/auth_controller.dart';
import '../network/api_client.dart';

/// The Web Push (VAPID) public key of the Firebase project, given at build
/// time. Not a secret; without it the web build simply gets no push.
const _vapidKey = String.fromEnvironment('NEOPRINT_FCM_VAPID_KEY');

/// A push the user opened, or one that arrived while the app was in front.
class PushEvent {
  const PushEvent({required this.notificationId, required this.link});

  /// From the push's data (docs/API.md "Bildirişlər").
  factory PushEvent.fromData(Map<String, dynamic> data) => PushEvent(
    notificationId: data['notificationId'] as String?,
    link: data['link'] as String?,
  );

  final String? notificationId;
  final String? link;
}

/// What the app needs from the phone's push service; Firebase on Android,
/// iOS and the web, nothing elsewhere (and in tests).
abstract interface class PushPlatform {
  /// Starts it and asks the user once; null when push is not available here
  /// or the user said no.
  Future<String?> token();

  Stream<String> get tokenRefreshed;

  /// Pushes that came while the app was open (the phone shows none then).
  Stream<PushEvent> get foreground;

  /// Pushes the user tapped while the app was in the background.
  Stream<PushEvent> get opened;

  /// The push that started the app, if one did.
  Future<PushEvent?> launchedBy();

  /// The token this device has now, without asking the user for anything;
  /// null when it has none or push is not available here.
  Future<String?> current();

  /// Gives the device's token up: nothing sent to it arrives any more, and
  /// the next [token] is a new one (T-268).
  Future<void> forget();

  /// `android`, `ios` or `web`.
  String get platform;
}

/// No push on this device: the list and the in-app banner still work.
class NoPushPlatform implements PushPlatform {
  const NoPushPlatform();

  @override
  Future<String?> token() async => null;

  @override
  Stream<String> get tokenRefreshed => const Stream.empty();

  @override
  Stream<PushEvent> get foreground => const Stream.empty();

  @override
  Stream<PushEvent> get opened => const Stream.empty();

  @override
  Future<PushEvent?> launchedBy() async => null;

  @override
  Future<String?> current() async => null;

  @override
  Future<void> forget() async {}

  @override
  String get platform => 'none';
}

/// FlutterFire (plan section 2.0): firebase_core + firebase_messaging, the
/// configuration in lib/firebase_options.dart.
class FirebasePushPlatform implements PushPlatform {
  Future<bool>? _started;

  static bool get supported =>
      kIsWeb ||
      defaultTargetPlatform == TargetPlatform.android ||
      defaultTargetPlatform == TargetPlatform.iOS;

  Future<bool> _start() => _started ??= () async {
    try {
      await Firebase.initializeApp(
        options: DefaultFirebaseOptions.currentPlatform,
      );
      // In front the app shows its own banner instead of the system one.
      await FirebaseMessaging.instance
          .setForegroundNotificationPresentationOptions();
      return true;
    } on Object catch (error) {
      debugPrint('Push unavailable: $error');
      return false;
    }
  }();

  @override
  Future<String?> token() async {
    if (!await _start()) {
      return null;
    }

    if (kIsWeb && _vapidKey.isEmpty) {
      debugPrint(
        'Push unavailable on the web: NEOPRINT_FCM_VAPID_KEY not set.',
      );
      return null;
    }

    try {
      final settings = await FirebaseMessaging.instance.requestPermission();
      if (settings.authorizationStatus == AuthorizationStatus.denied) {
        return null;
      }

      return await FirebaseMessaging.instance.getToken(
        vapidKey: kIsWeb ? _vapidKey : null,
      );
    } on Object catch (error) {
      debugPrint('Push token unavailable: $error');
      return null;
    }
  }

  @override
  Stream<String> get tokenRefreshed =>
      _whenStarted(() => FirebaseMessaging.instance.onTokenRefresh);

  @override
  Stream<PushEvent> get foreground => _whenStarted(
    () => FirebaseMessaging.onMessage.map((m) => PushEvent.fromData(m.data)),
  );

  @override
  Stream<PushEvent> get opened => _whenStarted(
    () => FirebaseMessaging.onMessageOpenedApp.map(
      (m) => PushEvent.fromData(m.data),
    ),
  );

  @override
  Future<PushEvent?> launchedBy() async {
    if (kIsWeb || !await _start()) {
      return null;
    }

    final message = await FirebaseMessaging.instance.getInitialMessage();
    return message == null ? null : PushEvent.fromData(message.data);
  }

  @override
  Future<String?> current() async {
    if (!await _start() || (kIsWeb && _vapidKey.isEmpty)) {
      return null;
    }

    try {
      // Not asked for here: a browser that was never allowed to notify has
      // no token, and signing out is no moment to ask.
      final settings = await FirebaseMessaging.instance
          .getNotificationSettings();
      if (kIsWeb &&
          settings.authorizationStatus != AuthorizationStatus.authorized) {
        return null;
      }

      return await FirebaseMessaging.instance.getToken(
        vapidKey: kIsWeb ? _vapidKey : null,
      );
    } on Object catch (error) {
      debugPrint('Push token unavailable: $error');
      return null;
    }
  }

  @override
  Future<void> forget() async {
    if (!await _start()) {
      return;
    }

    try {
      await FirebaseMessaging.instance.deleteToken();
    } on Object catch (error) {
      // Offline, most likely: the server-side removal is the other half.
      debugPrint('Push token not given up: $error');
    }
  }

  @override
  String get platform => kIsWeb
      ? 'web'
      : defaultTargetPlatform == TargetPlatform.iOS
      ? 'ios'
      : 'android';

  Stream<T> _whenStarted<T>(Stream<T> Function() stream) async* {
    if (await _start()) {
      yield* stream();
    }
  }
}

final pushPlatformProvider = Provider<PushPlatform>(
  (ref) => FirebasePushPlatform.supported
      ? FirebasePushPlatform()
      : const NoPushPlatform(),
);

/// `POST api/users/me/fcm-token` (docs/API.md "Bildirişlər"). The token is
/// taken off the account by the sign-out itself (`POST auth/logout`, T-268).
class PushDeviceApi {
  PushDeviceApi(this._api);

  final ApiClient _api;

  Future<void> register({
    required String token,
    required String language,
    required String platform,
  }) => _api.send<void>(
    method: 'POST',
    path: 'api/users/me/fcm-token',
    body: {'token': token, 'language': language, 'platform': platform},
    parse: (_) {},
  );
}

final pushDeviceApiProvider = Provider<PushDeviceApi>(
  (ref) => PushDeviceApi(ref.watch(apiClientProvider)),
);

/// This device's registration for the signed-in user's push.
class PushRegistration {
  PushRegistration(this._api, this._platform);

  final PushDeviceApi _api;
  final PushPlatform _platform;
  String? _token;
  StreamSubscription<String>? _refresh;

  /// The app's language now: a renewed token is registered in it, not in the
  /// one the app had at sign-in (T-152).
  String? language;

  /// The token sent last, for signing out.
  String? get token => _token;

  /// Registers this device, its pushes written in [language], and again
  /// whenever FCM renews the token.
  Future<void> register(String language) async {
    final token = await _platform.token();
    if (token == null) {
      return;
    }

    this.language ??= language;
    await _send(token, this.language!);
    await _refresh?.cancel();
    _refresh = _platform.tokenRefreshed.listen(
      (fresh) => unawaited(
        _send(fresh, this.language ?? language).catchError((Object _) {}),
      ),
    );
  }

  /// How long signing out waits for the phone's push service.
  static const releaseWithin = Duration(seconds: 3);

  /// Signing out: this device stops getting the user's push (T-268).
  ///
  /// The token is asked of the phone, not remembered from this run: one
  /// registered on an earlier start is still on the account even when this
  /// run never got as far as registering - which is how a signed-out phone
  /// kept getting push. The phone then gives the token up.
  ///
  /// Returns the token, for the sign-out request to take off the account.
  /// Best effort: signing out never waits on it for long.
  Future<String?> release() async {
    await _refresh?.cancel();
    _refresh = null;

    final token =
        _token ??
        await _platform.current().timeout(releaseWithin, onTimeout: () => null);
    _token = null;
    // The next account registers in its own language.
    language = null;

    await _platform.forget().timeout(releaseWithin, onTimeout: () {});

    return token;
  }

  Future<void> _send(String token, String language) async {
    await _api.register(
      token: token,
      language: language,
      platform: _platform.platform,
    );
    _token = token;
  }
}

/// One for the app. It does not follow who is signed in: the auth controller
/// reads it when signing out, and a provider that watched the signed-in user
/// would depend on the very controller that reads it - which Riverpod refuses
/// with a circular dependency error. That error was swallowed by the sign-out's
/// "best effort" catch, so signing out never took the device off the account
/// (T-268). [PushRegistration.release] leaves it clean for the next account.
final pushRegistrationProvider = Provider<PushRegistration>(
  (ref) => PushRegistration(
    ref.watch(pushDeviceApiProvider),
    ref.watch(pushPlatformProvider),
  ),
);
