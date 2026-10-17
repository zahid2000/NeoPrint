import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/auth/auth_controller.dart';
import 'package:neoprint/core/auth/auth_models.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/push/push_service.dart';
import 'package:neoprint/core/storage/secure_storage.dart';
import 'package:neoprint/features/auth/splash_screen.dart';
import 'package:neoprint/l10n/app_localizations.dart';

import 'session_kit.dart';

void main() {
  // T-269 (the owner, 2026-10-14): "signed in, closed the app, opened it again -
  // it asks me to sign in again".
  group('opening the app again', () {
    testWidgets(
      'a server that cannot be reached is not a sign-out: the app waits and '
      'tries again',
      (tester) async {
        final app = wire();
        addTearDown(app.container.dispose);

        app.server
          ..then('/api/auth/refresh', unreachable)
          ..then('/api/auth/refresh', unreachable)
          ..then('/api/auth/refresh', session());

        app.container.read(authControllerProvider);
        await settle(tester);

        var state = app.container.read(authControllerProvider);
        expect(
          state.status,
          AuthStatus.restoring,
          reason: 'not the sign-in screen',
        );
        expect(state.restoreStalled, isTrue);
        expect(app.storage.values[SecureStorageKeys.refreshToken], 'refresh-1');

        // By itself, a few seconds later - and still out of reach.
        await tester.pump(AuthController.retryRestoreAfter);
        await settle(tester);
        state = app.container.read(authControllerProvider);
        expect(state.status, AuthStatus.restoring);
        expect(state.restoreStalled, isTrue);

        // The server is back.
        await tester.pump(AuthController.retryRestoreAfter);
        await settle(tester);
        state = app.container.read(authControllerProvider);
        expect(state.status, AuthStatus.signedIn);
        expect(state.user?.email, 'user@example.com');
        expect(app.storage.values[SecureStorageKeys.refreshToken], 'refresh-2');
      },
    );

    for (final status in [429, 500, 502, 503]) {
      testWidgets('a server that answers $status keeps the stored session', (
        tester,
      ) async {
        final app = wire();
        addTearDown(app.container.dispose);

        app.server
          ..then('/api/auth/refresh', (status: status, body: null))
          ..then('/api/auth/refresh', session());

        app.container.read(authControllerProvider);
        await settle(tester);

        // Before the fix a "too many requests" threw the token away, and the
        // next start had nothing to restore.
        expect(app.storage.values[SecureStorageKeys.refreshToken], 'refresh-1');
        expect(
          app.container.read(authControllerProvider).status,
          AuthStatus.restoring,
        );

        await tester.pump(AuthController.retryRestoreAfter);
        await settle(tester);
        expect(
          app.container.read(authControllerProvider).status,
          AuthStatus.signedIn,
        );
      });
    }

    testWidgets('a token the server refuses is the end of the session', (
      tester,
    ) async {
      final app = wire();
      addTearDown(app.container.dispose);

      app.server.then('/api/auth/refresh', (
        status: 400,
        body: {
          'title': 'Validation failed',
          'errors': {
            'refreshToken': ['Sessiya bitib. Yenidən daxil olun.'],
          },
        },
      ));

      app.container.read(authControllerProvider);
      await settle(tester);

      expect(
        app.container.read(authControllerProvider).status,
        AuthStatus.signedOut,
      );
      expect(app.storage.values[SecureStorageKeys.refreshToken], isNull);

      // And nothing is tried again.
      await tester.pump(AuthController.retryRestoreAfter * 3);
      await settle(tester);
      expect(app.server.sent, hasLength(1));
    });

    testWidgets(
      'nothing stored is a fresh install: straight to sign-in, no request',
      (tester) async {
        final app = wire(storedToken: null);
        addTearDown(app.container.dispose);

        app.container.read(authControllerProvider);
        await settle(tester);

        expect(
          app.container.read(authControllerProvider).status,
          AuthStatus.signedOut,
        );
        expect(
          app.server.sent.where((r) => r.path == '/api/auth/refresh'),
          isEmpty,
        );
      },
    );

    testWidgets('the retry button asks at once', (tester) async {
      final app = wire();
      addTearDown(app.container.dispose);

      app.server
        ..then('/api/auth/refresh', unreachable)
        ..then('/api/auth/refresh', session());

      app.container.read(authControllerProvider);
      await settle(tester);

      app.container.read(authControllerProvider.notifier).restore();
      await settle(tester);

      expect(
        app.container.read(authControllerProvider).status,
        AuthStatus.signedIn,
      );

      // The timer that was waiting does not ask a second time.
      await tester.pump(AuthController.retryRestoreAfter * 2);
      await settle(tester);
      expect(app.server.sent, hasLength(2));
    });

    testWidgets('a request whose session cannot be renewed just now fails as '
        '"unreachable" and stays signed in', (tester) async {
      final app = wire();
      addTearDown(app.container.dispose);

      app.server.then('/api/auth/refresh', session());
      app.container.read(authControllerProvider);
      await settle(tester);

      // The access token has run out, and the server is restarting.
      app.server
        ..then('/api/wallet', (status: 401, body: null))
        ..then('/api/auth/refresh', (status: 503, body: null));

      Object? failure;
      app.container
          .read(apiClientProvider)
          .send<void>(method: 'GET', path: 'api/wallet', parse: (_) {})
          .catchError((Object error) => failure = error);
      await settle(tester);

      expect(failure, isA<ApiException>());
      expect((failure! as ApiException).kind, ApiErrorKind.unreachable);
      expect(
        app.container.read(authControllerProvider).status,
        AuthStatus.signedIn,
      );
      expect(app.storage.values[SecureStorageKeys.refreshToken], 'refresh-2');
    });

    test('start-up and a request that starts with it spend the refresh token '
        'once', () async {
      final app = wire();
      addTearDown(app.container.dispose);

      // One answer only: a second exchange of the same token would be a
      // replay, which the server answers by ending every session.
      app.server.then('/api/auth/refresh', session());

      final api = app.container.read(apiClientProvider);
      final results = await Future.wait([
        api.renewSession(),
        api.renewSession(deviceId: 'device-1'),
        api.freshAccessToken(),
      ]);

      expect((results[0]! as RenewedSession).outcome, SessionRenewal.renewed);
      expect((results[1]! as RenewedSession).outcome, SessionRenewal.renewed);
      expect(results[2], 'access-1');
      expect(
        app.server.sent.where((r) => r.path == '/api/auth/refresh'),
        hasLength(1),
      );
    });
  });

  // T-268 (the owner, 2026-10-14): "I sign out and the push still comes".
  group('signing out and push', () {
    testWidgets(
      "the device's push token leaves the account with the sign-out, and "
      'the phone gives it up - even when this run never registered it',
      (tester) async {
        final app = wire();
        addTearDown(app.container.dispose);

        app.server
          ..then('/api/auth/refresh', session())
          ..then('/api/auth/logout', (status: 204, body: null));

        app.container.read(authControllerProvider);
        await settle(tester);

        // Nothing registered a token in this run: the one on the account is
        // from an earlier start.
        expect(app.container.read(pushRegistrationProvider).token, isNull);

        app.container.read(authControllerProvider.notifier).signOut();
        await settle(tester);

        final logout = app.server.sent.singleWhere(
          (r) => r.path == '/api/auth/logout',
        );
        expect(logout.body, {'refreshToken': 'refresh-2', 'fcmToken': 'fcm-1'});
        expect(app.push.forgotten, 1, reason: 'the phone no longer has it');
        expect(
          app.container.read(authControllerProvider).status,
          AuthStatus.signedOut,
        );
        expect(app.storage.values[SecureStorageKeys.refreshToken], isNull);
      },
    );

    testWidgets('a session the server ends takes the push off the phone too', (
      tester,
    ) async {
      final app = wire();
      addTearDown(app.container.dispose);

      app.server.then('/api/auth/refresh', session());
      app.container.read(authControllerProvider);
      await settle(tester);

      // Signed out everywhere from another device.
      app.server
        ..then('/api/wallet', (status: 401, body: null))
        ..then('/api/auth/refresh', (status: 400, body: null));

      app.container
          .read(apiClientProvider)
          .send<void>(method: 'GET', path: 'api/wallet', parse: (_) {})
          .catchError((Object _) {});
      await settle(tester);

      expect(
        app.container.read(authControllerProvider).status,
        AuthStatus.signedOut,
      );
      expect(app.push.forgotten, 1);
    });
  });

  testWidgets('the start screen says the server is out of reach and offers to '
      'try again', (tester) async {
    final app = wire();
    addTearDown(app.container.dispose);

    app.server
      ..then('/api/auth/refresh', unreachable)
      ..then('/api/auth/refresh', session());

    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: app.container,
        child: const MaterialApp(
          locale: Locale('az'),
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          home: SplashScreen(),
        ),
      ),
    );

    // Being asked: the spinner.
    expect(find.byType(CircularProgressIndicator), findsOneWidget);

    await settle(tester);

    expect(find.byType(CircularProgressIndicator), findsNothing);
    expect(find.text('Server cavab vermir'), findsOneWidget);
    expect(find.textContaining('Hesabınızdan çıxılmayıb'), findsOneWidget);
    expect(find.byIcon(Icons.cloud_off_outlined), findsOneWidget);
    expect(find.text('İnternet yoxdur'), findsNothing);

    await tester.tap(find.widgetWithText(FilledButton, 'Yenidən cəhd et'));
    await settle(tester);

    expect(
      app.container.read(authControllerProvider).status,
      AuthStatus.signedIn,
    );
  });

  // T-199 (2026-10-11): opened without a network, the app showed the sign-in
  // screen with no word of why.
  testWidgets('opened without internet, the start screen says so - not the '
      'sign-in screen - and opens by itself once the phone is online', (
    tester,
  ) async {
    final app = wire();
    addTearDown(app.container.dispose);

    app.server
      ..then('/api/auth/refresh', noInternet)
      ..then('/api/auth/refresh', noInternet)
      ..then('/api/auth/refresh', session());

    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: app.container,
        child: const MaterialApp(
          locale: Locale('az'),
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          home: SplashScreen(),
        ),
      ),
    );
    await settle(tester);

    var state = app.container.read(authControllerProvider);
    expect(
      state.status,
      AuthStatus.restoring,
      reason: 'not the sign-in screen',
    );
    expect(state.restoreOffline, isTrue);
    expect(find.text('İnternet yoxdur'), findsOneWidget);
    expect(find.byIcon(Icons.wifi_off), findsOneWidget);
    expect(find.textContaining('Hesabınızdan çıxılmayıb'), findsOneWidget);
    expect(find.text('Server cavab vermir'), findsNothing);
    expect(app.storage.values[SecureStorageKeys.refreshToken], 'refresh-1');

    // The button asks at once; still offline, the screen stays as it was.
    await tester.tap(find.widgetWithText(FilledButton, 'Yenidən cəhd et'));
    await settle(tester);
    expect(find.text('İnternet yoxdur'), findsOneWidget);
    expect(app.server.sent, hasLength(2));

    // Online again: by itself, a few seconds later.
    await tester.pump(AuthController.retryRestoreAfter);
    await settle(tester);
    state = app.container.read(authControllerProvider);
    expect(state.status, AuthStatus.signedIn);
  });

  test('"no internet" is told from "the server does not answer" by the '
      "platform's own words", () {
    DioException failure(Object? error, {DioExceptionType? type}) =>
        DioException(
          requestOptions: RequestOptions(path: 'api/auth/refresh'),
          type: type ?? DioExceptionType.connectionError,
          error: error,
        );

    for (final offline in [
      "SocketException: Failed host lookup: 'api.neoprint.az' (OS Error: No address associated with hostname, errno = 7)",
      'SocketException: Network is unreachable (OS Error: Network is unreachable, errno = 101)',
      'SocketException: nodename nor servname provided, or not known',
    ]) {
      expect(ApiClient.isNoInternet(failure(offline)), isTrue, reason: offline);
    }

    for (final server in [
      'SocketException: Connection refused (OS Error: Connection refused, errno = 111)',
      'SocketException: Connection reset by peer',
      null,
    ]) {
      expect(ApiClient.isNoInternet(failure(server)), isFalse, reason: server);
    }

    expect(
      ApiClient.isNoInternet(
        failure('Failed host lookup', type: DioExceptionType.connectionTimeout),
      ),
      isFalse,
      reason: 'a timeout is the server being slow',
    );
  });
}
