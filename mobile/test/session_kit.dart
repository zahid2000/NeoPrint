// The app's session over a scripted server, for the tests of signing in,
// restoring, signing out (T-268, T-269) and the app's lock (T-270).
import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/auth/auth_controller.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/push/push_service.dart';
import 'package:neoprint/core/security/biometrics.dart';
import 'package:neoprint/core/storage/secure_storage.dart';

/// One answer of the server: a status and a body, or no answer at all.
typedef Step = ({int? status, Map<String, dynamic>? body});

const Step unreachable = (status: null, body: null);

/// A transport that plays its steps for a path in order, and remembers what
/// it was sent. Written by hand: plan section 11 rules out a mocking library.
final class SteppedAdapter implements HttpClientAdapter {
  final Map<String, List<Step>> steps = {};
  final List<({String path, Object? body})> sent = [];

  void then(String path, Step step) =>
      steps.putIfAbsent(path, () => []).add(step);

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<List<int>>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    final path = options.uri.path;
    sent.add((path: path, body: options.data));

    final queue = steps[path];
    if (queue == null || queue.isEmpty) {
      throw StateError('No step for ${options.method} $path');
    }

    final step = queue.removeAt(0);
    if (step.status == null) {
      throw DioException.connectionError(
        requestOptions: options,
        reason: 'No connection',
      );
    }

    return ResponseBody.fromString(
      jsonEncode(step.body ?? const <String, dynamic>{}),
      step.status!,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

Step session({String refreshToken = 'refresh-2'}) => (
  status: 200,
  body: {
    'accessToken': 'access-1',
    'accessTokenExpiresAt': DateTime.now()
        .toUtc()
        .add(const Duration(minutes: 15))
        .toIso8601String(),
    'refreshToken': refreshToken,
    'user': {
      'id': 'a1b2c3',
      'publicId': 'NP-ABC234',
      'email': 'user@example.com',
      'fullName': 'Test User',
      'language': 'az',
      'emailConfirmed': true,
      'pendingConsents': const <Map<String, dynamic>>[],
    },
  },
);

/// The phone's push service: it has a token whether or not this run of the
/// app registered it.
class DevicePush extends NoPushPlatform {
  DevicePush({this.deviceToken = 'fcm-1'});

  final String? deviceToken;
  int forgotten = 0;

  @override
  Future<String?> current() async => forgotten > 0 ? null : deviceToken;

  @override
  Future<void> forget() async => forgotten++;

  @override
  String get platform => 'android';
}

/// The phone's fingerprint or face check, answered as the test says.
class FakeBiometrics implements Biometrics {
  bool enrolled = true;
  bool passes = true;
  int asked = 0;

  @override
  Future<bool> available() async => enrolled;

  @override
  Future<bool> authenticate({
    required String title,
    required String cancel,
  }) async {
    asked++;
    return enrolled && passes;
  }
}

/// The app's session wiring over a scripted server.
({
  ProviderContainer container,
  SteppedAdapter server,
  InMemorySecureStorage storage,
  DevicePush push,
  FakeBiometrics biometrics,
})
wire({String? storedToken = 'refresh-1'}) {
  final server = SteppedAdapter();
  final storage = InMemorySecureStorage();
  final push = DevicePush();
  final biometrics = FakeBiometrics();

  if (storedToken != null) {
    storage.write(SecureStorageKeys.refreshToken, storedToken);
  }

  final container = ProviderContainer(
    overrides: [
      secureStorageProvider.overrideWithValue(storage),
      pushPlatformProvider.overrideWithValue(push),
      biometricsProvider.overrideWithValue(biometrics),
      apiClientProvider.overrideWith((ref) {
        final client = ApiClient(
          baseUrl: 'http://backend.test/',
          storage: storage,
          tokens: ref.watch(accessTokenProvider),
          dio: Dio(
            BaseOptions(
              baseUrl: 'http://backend.test/',
              validateStatus: (_) => true,
            ),
          )..httpClientAdapter = server,
        );

        client.onSessionExpired = () =>
            ref.read(authControllerProvider.notifier).onSessionExpired();

        return client;
      }),
    ],
  );

  return (
    container: container,
    server: server,
    storage: storage,
    push: push,
    biometrics: biometrics,
  );
}

/// Lets the scripted server's answer travel all the way back: the client's
/// pipeline takes several turns of the event loop, on the tester's clock.
Future<void> settle(WidgetTester tester) async {
  for (var turn = 0; turn < 10; turn++) {
    await tester.pump(const Duration(milliseconds: 1));
  }
}
