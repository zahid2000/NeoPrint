import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/auth/auth_controller.dart';
import 'package:neoprint/core/auth/auth_models.dart';
import 'package:neoprint/core/security/app_lock.dart';
import 'package:neoprint/core/storage/secure_storage.dart';
import 'package:neoprint/features/security/pin_screen.dart';
import 'package:neoprint/l10n/app_localizations.dart';

import 'session_kit.dart';

/// The account of [session]: its id is what a PIN is kept with.
const user = 'a1b2c3';

/// Signs in with the password, the way the sign-in screen does.
Future<void> signIn(
  WidgetTester tester,
  ({
    ProviderContainer container,
    SteppedAdapter server,
    InMemorySecureStorage storage,
    DevicePush push,
    FakeBiometrics biometrics,
  })
  app,
) async {
  app.server.then('/api/auth/login', session());
  app.container
      .read(authControllerProvider.notifier)
      .signIn(email: 'user@example.com', password: 'Password123');
  await settle(tester);
}

/// Waits for [work] on the tester's clock: a sign-out goes to the scripted
/// server, and that answer only travels while the clock is pumped.
Future<T> pumped<T>(WidgetTester tester, Future<T> work) async {
  late T result;
  var done = false;
  work.then((value) {
    result = value;
    done = true;
  });
  await settle(tester);
  expect(done, isTrue, reason: 'it finished');
  return result;
}

AppLockStatus lockOf(ProviderContainer container) =>
    container.read(appLockProvider).status;

void main() {
  // T-270 (the owner, 2026-10-14): a mandatory 4-digit PIN, asked at start and
  // after five minutes away, five wrong ones sign the account out, and the
  // fingerprint or face where the phone has one.
  group('the app lock', () {
    testWidgets('a PIN is set after signing in with the password, before '
        'anything else', (tester) async {
      final app = wire(storedToken: null);
      addTearDown(app.container.dispose);
      app.container.read(appLockProvider);
      await settle(tester);

      expect(lockOf(app.container), AppLockStatus.off, reason: 'signed out');

      await signIn(tester, app);
      expect(lockOf(app.container), AppLockStatus.setup);

      await app.container.read(appLockProvider.notifier).setPin('2468');
      expect(lockOf(app.container), AppLockStatus.unlocked);
      expect(app.storage.values[SecureStorageKeys.pin], '$user|2468');
    });

    testWidgets('a phone that starts with nobody signed in keeps no PIN', (
      tester,
    ) async {
      final app = wire(storedToken: null);
      addTearDown(app.container.dispose);
      await app.storage.write(SecureStorageKeys.pin, '$user|2468');
      await app.storage.write(SecureStorageKeys.pinFailures, '3');

      app.container.read(appLockProvider);
      await settle(tester);

      expect(app.storage.values[SecureStorageKeys.pin], isNull);
      expect(app.storage.values[SecureStorageKeys.pinFailures], isNull);

      // So the next sign-in sets one.
      await signIn(tester, app);
      expect(lockOf(app.container), AppLockStatus.setup);
    });

    testWidgets('a session restored at start is locked until the PIN', (
      tester,
    ) async {
      final app = wire();
      addTearDown(app.container.dispose);
      await app.storage.write(SecureStorageKeys.pin, '$user|2468');
      app.server.then('/api/auth/refresh', session());

      app.container.read(appLockProvider);
      await settle(tester);

      expect(
        app.container.read(authControllerProvider).status,
        AuthStatus.signedIn,
      );
      expect(lockOf(app.container), AppLockStatus.locked);

      final lock = app.container.read(appLockProvider.notifier);
      final wrong = await lock.unlock('1111');
      expect(wrong.unlocked, isFalse);
      expect(wrong.attemptsLeft, AppLockController.maxFailures - 1);
      // Kept where killing the app does not reset it.
      expect(app.storage.values[SecureStorageKeys.pinFailures], '1');

      final right = await lock.unlock('2468');
      expect(right.unlocked, isTrue);
      expect(lockOf(app.container), AppLockStatus.unlocked);
      expect(app.storage.values[SecureStorageKeys.pinFailures], isNull);
    });

    testWidgets('a PIN of another account on this phone is no PIN', (
      tester,
    ) async {
      final app = wire();
      addTearDown(app.container.dispose);
      await app.storage.write(SecureStorageKeys.pin, 'someone-else|2468');
      app.server.then('/api/auth/refresh', session());

      app.container.read(appLockProvider);
      await settle(tester);

      expect(lockOf(app.container), AppLockStatus.setup);
    });

    testWidgets('five wrong PINs in a row sign the account out on this phone, '
        'and the PIN is forgotten', (tester) async {
      final app = wire();
      addTearDown(app.container.dispose);
      await app.storage.write(SecureStorageKeys.pin, '$user|2468');
      app.server
        ..then('/api/auth/refresh', session())
        ..then('/api/auth/logout', (status: 204, body: null));

      app.container.read(appLockProvider);
      await settle(tester);

      final lock = app.container.read(appLockProvider.notifier);
      final left = <int>[];
      for (var i = 0; i < AppLockController.maxFailures; i++) {
        left.add((await pumped(tester, lock.unlock('0000'))).attemptsLeft);
      }

      expect(left, [4, 3, 2, 1, 0]);
      expect(
        app.container.read(authControllerProvider).status,
        AuthStatus.signedOut,
      );
      expect(lockOf(app.container), AppLockStatus.off);
      expect(app.storage.values[SecureStorageKeys.pin], isNull);
      expect(app.storage.values[SecureStorageKeys.pinFailures], isNull);
      expect(app.storage.values[SecureStorageKeys.refreshToken], isNull);
      expect(
        app.server.sent.where((r) => r.path == '/api/auth/logout'),
        hasLength(1),
      );
    });

    testWidgets('back after five minutes away it is locked; after a shorter '
        'absence it is not', (tester) async {
      final app = wire(storedToken: null);
      addTearDown(app.container.dispose);
      app.container.read(appLockProvider);
      await settle(tester);
      await signIn(tester, app);

      final lock = app.container.read(appLockProvider.notifier);
      await lock.setPin('2468');
      expect(lockOf(app.container), AppLockStatus.unlocked);

      final away = DateTime(2026, 10, 14, 9);

      // Choosing a file, taking a photo.
      lock
        ..appHidden(away)
        ..appShown(away.add(const Duration(minutes: 4, seconds: 59)));
      expect(lockOf(app.container), AppLockStatus.unlocked);

      lock
        ..appHidden(away)
        ..appShown(away.add(AppLockController.lockAfter));
      expect(lockOf(app.container), AppLockStatus.locked);
    });

    testWidgets('the fingerprint or face opens it when switched on, and only '
        'when it passes', (tester) async {
      final app = wire();
      addTearDown(app.container.dispose);
      await app.storage.write(SecureStorageKeys.pin, '$user|2468');
      await app.storage.write(SecureStorageKeys.pinBiometrics, user);
      app.server.then('/api/auth/refresh', session());

      app.container.read(appLockProvider);
      await settle(tester);

      expect(app.container.read(appLockProvider).biometrics, isTrue);

      final lock = app.container.read(appLockProvider.notifier);
      app.biometrics.passes = false;
      expect(await lock.unlockWithBiometrics(title: 't', cancel: 'c'), isFalse);
      expect(lockOf(app.container), AppLockStatus.locked);

      app.biometrics.passes = true;
      expect(await lock.unlockWithBiometrics(title: 't', cancel: 'c'), isTrue);
      expect(lockOf(app.container), AppLockStatus.unlocked);
    });

    testWidgets('switched on for a phone that no longer has a finger or face '
        'enrolled, it is not offered', (tester) async {
      final app = wire();
      addTearDown(app.container.dispose);
      await app.storage.write(SecureStorageKeys.pin, '$user|2468');
      await app.storage.write(SecureStorageKeys.pinBiometrics, user);
      app.biometrics.enrolled = false;
      app.server.then('/api/auth/refresh', session());

      app.container.read(appLockProvider);
      await settle(tester);

      expect(app.container.read(appLockProvider).biometrics, isFalse);
    });
  });

  group('the PIN screen', () {
    Future<void> show(
      WidgetTester tester,
      ProviderContainer container,
      PinMode mode,
    ) async {
      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: container,
          child: MaterialApp(
            locale: const Locale('az'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            home: PinScreen(mode: mode),
          ),
        ),
      );
      await settle(tester);
    }

    Future<void> type(WidgetTester tester, String pin) async {
      for (final digit in pin.split('')) {
        await tester.tap(find.byKey(ValueKey('pin-key-$digit')));
        await tester.pump();
      }
      await settle(tester);
    }

    testWidgets('setting it up: typed twice; two that differ start again', (
      tester,
    ) async {
      final app = wire(storedToken: null);
      addTearDown(app.container.dispose);
      app.container.read(appLockProvider);
      await settle(tester);
      await signIn(tester, app);
      app.biometrics.enrolled = false;

      await show(tester, app.container, PinMode.setup);
      expect(find.text('PIN kod qurun'), findsOneWidget);

      await type(tester, '1357');
      expect(find.text('PIN-i təkrarlayın'), findsOneWidget);

      await type(tester, '1358');
      expect(find.textContaining('üst-üstə düşmür'), findsOneWidget);
      expect(find.text('PIN kod qurun'), findsOneWidget);
      expect(lockOf(app.container), AppLockStatus.setup);

      await type(tester, '1357');
      await type(tester, '1357');
      expect(lockOf(app.container), AppLockStatus.unlocked);
      expect(app.storage.values[SecureStorageKeys.pin], '$user|1357');
    });

    testWidgets('setting it up offers the fingerprint or face where the phone '
        'has one', (tester) async {
      final app = wire(storedToken: null);
      addTearDown(app.container.dispose);
      app.container.read(appLockProvider);
      await settle(tester);
      await signIn(tester, app);

      await show(tester, app.container, PinMode.setup);
      await type(tester, '1357');
      await type(tester, '1357');

      expect(find.text('Barmaq izi və ya üz ilə açılsın?'), findsOneWidget);
      await tester.tap(find.text('Bəli'));
      await settle(tester);

      expect(app.container.read(appLockProvider).biometrics, isTrue);
      expect(app.storage.values[SecureStorageKeys.pinBiometrics], user);
    });

    testWidgets('the lock says how many tries are left, and opens with the '
        'right PIN', (tester) async {
      final app = wire();
      addTearDown(app.container.dispose);
      await app.storage.write(SecureStorageKeys.pin, '$user|2468');
      app.server.then('/api/auth/refresh', session());
      app.container.read(appLockProvider);
      await settle(tester);

      await show(tester, app.container, PinMode.unlock);
      expect(find.text('PIN kodu daxil edin'), findsOneWidget);
      expect(find.text('user@example.com hesabı'), findsOneWidget);
      expect(
        find.byIcon(Icons.fingerprint),
        findsNothing,
        reason: 'not switched on',
      );

      await type(tester, '1111');
      expect(find.textContaining('Qalan cəhd: 4'), findsOneWidget);

      await type(tester, '2468');
      expect(lockOf(app.container), AppLockStatus.unlocked);
    });

    testWidgets('with the fingerprint switched on the prompt comes by itself '
        'as the lock opens', (tester) async {
      final app = wire();
      addTearDown(app.container.dispose);
      await app.storage.write(SecureStorageKeys.pin, '$user|2468');
      await app.storage.write(SecureStorageKeys.pinBiometrics, user);
      app.server.then('/api/auth/refresh', session());
      app.container.read(appLockProvider);
      await settle(tester);

      await show(tester, app.container, PinMode.unlock);

      expect(app.biometrics.asked, 1);
      expect(lockOf(app.container), AppLockStatus.unlocked);
    });

    testWidgets('"PIN-i unutdum" asks first, then signs out', (tester) async {
      final app = wire();
      addTearDown(app.container.dispose);
      await app.storage.write(SecureStorageKeys.pin, '$user|2468');
      app.server
        ..then('/api/auth/refresh', session())
        ..then('/api/auth/logout', (status: 204, body: null));
      app.container.read(appLockProvider);
      await settle(tester);

      await show(tester, app.container, PinMode.unlock);
      await tester.tap(find.text('PIN-i unutdum'));
      await settle(tester);

      expect(find.text('PIN-i unutmusunuz?'), findsOneWidget);
      await tester.tap(find.text('Çıxış et'));
      await settle(tester);

      expect(
        app.container.read(authControllerProvider).status,
        AuthStatus.signedOut,
      );
      expect(app.storage.values[SecureStorageKeys.pin], isNull);
    });
  });
}
