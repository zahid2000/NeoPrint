import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:neoprint/core/auth/auth_controller.dart';
import 'package:neoprint/core/features/feature_flags.dart';
import 'package:neoprint/core/features/readiness.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/storage/secure_storage.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/files/files_controller.dart';
import 'package:neoprint/features/files/files_screen.dart';
import 'package:neoprint/features/home/home_screen.dart';
import 'package:neoprint/features/prepared/prepared_models.dart';
import 'package:neoprint/features/prepared/prepared_repository.dart';
import 'package:neoprint/features/profile/profile_screen.dart';
import 'package:neoprint/features/schedule/schedule_models.dart';
import 'package:neoprint/features/schedule/schedule_repository.dart';
import 'package:neoprint/l10n/app_localizations.dart';
import 'package:neoprint/l10n/app_localizations_az.dart';
import 'package:neoprint/shared/api_error_text.dart';
import 'package:neoprint/shared/widgets/bottom_nav_shell.dart';

import 'files_screen_test.dart' show ListRepository;
import 'payments_kit.dart';
import 'prepared_test.dart' show FakePrepared, file;
import 'schedule_test.dart' show FakeSchedules, schedule;

/// Flags fixed for a test: no request, no refresh.
class FixedFlags extends FeatureFlagsController {
  FixedFlags(this.values);

  final Map<String, bool> values;

  @override
  FeatureFlags build() => FeatureFlags(values);

  @override
  Future<void> refresh() async {}
}

Override flags(Map<String, bool> values) =>
    featureFlagsProvider.overrideWith(() => FixedFlags(values));

Widget screen(Widget home, List<Override> overrides) => ProviderScope(
  overrides: overrides,
  child: MaterialApp(
    theme: AppTheme.light(),
    locale: const Locale('az'),
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: home,
  ),
);

PreparedPrint paidOrder() => PreparedPrint(
  isPaid: true,
  id: 'job1',
  fileId: 'f1',
  fileName: 'hesabat.pdf',
  pages: 3,
  state: PreparedState.ready,
  createdAt: DateTime(2026, 10, 3),
  kioskCode: 'KSK-9002',
  amount: 0.30,
);

void main() {
  test('a key the server did not send is on, as on the server', () {
    const flags = FeatureFlags({FeatureKey.scheduledPrint: false});

    expect(flags.isOn(FeatureKey.scheduledPrint), isFalse);
    expect(flags.isOn(FeatureKey.dataExport), isTrue);
    expect(FeatureFlags.unknown.isOn(FeatureKey.userQrPrint), isTrue);
  });

  testWidgets('switched off, the file menu offers neither preparing nor '
      'scheduling', (tester) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          filesRepositoryProvider.overrideWithValue(ListRepository([file])),
          userHubEventsProvider.overrideWithValue(const Stream.empty()),
          flags({
            FeatureKey.userQrPrint: false,
            FeatureKey.scheduledPrint: false,
          }),
        ],
        child: MaterialApp(
          theme: AppTheme.light(),
          locale: const Locale('az'),
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: const FilesScreen(),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.byTooltip('Əməliyyatlar').first);
    await tester.pumpAndSettle();

    expect(find.text('Kiosk üçün hazırla'), findsNothing);
    expect(find.text('Çapı planla'), findsNothing);
    // Not a disabled row: the rest of the menu is as before.
    expect(find.text('Avtomatik silinmə'), findsOneWidget);
  });

  testWidgets('switched on, the file menu has both', (tester) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          filesRepositoryProvider.overrideWithValue(ListRepository([file])),
          userHubEventsProvider.overrideWithValue(const Stream.empty()),
          flags(const {}),
        ],
        child: MaterialApp(
          theme: AppTheme.light(),
          locale: const Locale('az'),
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: const FilesScreen(),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.byTooltip('Əməliyyatlar').first);
    await tester.pumpAndSettle();

    expect(
      find.text('Kiosk üçün hazırla'),
      Readiness.personalQr ? findsOneWidget : findsNothing,
    );
    expect(
      find.text('Çapı planla'),
      Readiness.scheduledPrint ? findsOneWidget : findsNothing,
    );
  });

  testWidgets('the personal QR stays on the home tab while a paid order waits '
      'to be collected', (tester) async {
    final prepared = FakePrepared();
    final overrides = [
      ...step4Overrides(),
      preparedRepositoryProvider.overrideWithValue(prepared),
      flags({FeatureKey.userQrPrint: false}),
    ];

    await tester.pumpWidget(screen(const HomeScreen(), overrides));
    await tester.pumpAndSettle();
    expect(find.text('Mənim QR kodum'), findsNothing);
    expect(find.text('Hazırladıqlarım'), findsNothing);

    prepared.items = [paidOrder()];
    await tester.pumpWidget(const SizedBox());
    await tester.pumpWidget(screen(const HomeScreen(), overrides));
    await tester.pumpAndSettle();
    expect(find.text('Mənim QR kodum'), findsOneWidget);
    expect(find.text('Hazırladıqlarım'), findsOneWidget);

    // T-239: on a 360 dp phone each label is one line - side by side, half
    // the width broke "Hazırladıqlarım" in the middle of the word.
    for (final label in ['Mənim QR kodum', 'Hazırladıqlarım']) {
      final text = tester.renderObject<RenderParagraph>(find.text(label));
      expect(
        text.size.height,
        text.getMaxIntrinsicHeight(double.infinity),
        reason: '"$label" wraps',
      );
    }
  });

  testWidgets('"Məlumatlarımı yüklə" is not offered while it is off', (
    tester,
  ) async {
    await tester.pumpWidget(
      screen(const ProfileScreen(), [
        ...step4Overrides(),
        flags({FeatureKey.dataExport: false}),
      ]),
    );
    await tester.pumpAndSettle();

    expect(find.text('Məlumatlarımı yüklə'), findsNothing);
    expect(find.text('Hesabımı sil'), findsOneWidget);
  });

  group('the scheduled tab', () {
    Future<void> pumpShell(
      WidgetTester tester, {
      required List<ScheduledPrint> items,
      required bool on,
    }) async {
      final router = GoRouter(
        initialLocation: '/a',
        routes: [
          StatefulShellRoute.indexedStack(
            builder: (context, state, shell) => BottomNavShell(shell: shell),
            branches: [
              for (final path in ['/a', '/b', '/c', '/d', '/e'])
                StatefulShellBranch(
                  routes: [GoRoute(path: path, builder: (_, __) => Text(path))],
                ),
            ],
          ),
        ],
      );

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            currentUserProvider.overrideWithValue(null),
            userHubProvider.overrideWithValue(null),
            userHubEventsProvider.overrideWithValue(const Stream.empty()),
            userHubReconnectedProvider.overrideWithValue(const Stream.empty()),
            scheduleRepositoryProvider.overrideWithValue(
              FakeSchedules()..items = items,
            ),
            flags({FeatureKey.scheduledPrint: on}),
          ],
          child: MaterialApp.router(
            theme: AppTheme.light(),
            locale: const Locale('az'),
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            routerConfig: router,
          ),
        ),
      );
      await tester.pumpAndSettle();
    }

    testWidgets('is hidden when it is off and nothing is waiting', (
      tester,
    ) async {
      await pumpShell(tester, items: const [], on: false);

      expect(find.text('Planlı'), findsNothing);
      expect(
        find.byType(NavigationDestination),
        findsNWidgets(3 + (Readiness.kioskSearch ? 1 : 0)),
      );

      // The tabs after it still open their own branch.
      await tester.tap(find.text('Profil'));
      await tester.pumpAndSettle();
      expect(find.text('/e'), findsOneWidget);
    }, skip: !Readiness.scheduledPrint || !Readiness.kioskSearch);

    testWidgets('stays while a paid schedule is waiting', (tester) async {
      await pumpShell(
        tester,
        items: [schedule('s1', ScheduleStatus.scheduled)],
        on: false,
      );

      expect(find.text('Planlı'), findsOneWidget);
      await tester.tap(find.text('Planlı'));
      await tester.pumpAndSettle();
      expect(find.text('/d'), findsOneWidget);
    }, skip: !Readiness.scheduledPrint || !Readiness.kioskSearch);
  });

  group('a request refused because a feature is off', () {
    test('says what happened, that nothing was taken, and what to do', () {
      final l10n = AppLocalizationsAz();
      const off = ApiException(
        statusCode: 404,
        message: 'Bu funksiya hazırda əlçatan deyil.',
        code: 'features.unavailable',
        feature: FeatureKey.dataExport,
      );
      const kioskPay = ApiException(
        statusCode: 404,
        message: 'Bu funksiya hazırda əlçatan deyil.',
        code: 'features.unavailable',
        feature: FeatureKey.mobilePaymentAtKiosk,
      );

      expect(apiErrorText(l10n, off), l10n.errorFeatureOff);
      expect(apiErrorText(l10n, kioskPay), l10n.featureOffKioskPayment);
      expect(l10n.featureOffKioskPayment, contains('bank QR'));
    });

    test(
      'is read from the problem document and asks for the flags again',
      () async {
        final dio = Dio(
          BaseOptions(baseUrl: 'http://api.test/', validateStatus: (_) => true),
        );
        dio.httpClientAdapter = _Refusing();
        final client = ApiClient(
          baseUrl: 'http://api.test/',
          storage: const _NoStorage(),
          tokens: AccessTokenHolder(),
          dio: dio,
        );
        var asked = 0;
        client.onFeatureOff = () => asked++;

        await expectLater(
          client.send(
            method: 'POST',
            path: 'api/users/me/export',
            parse: (_) {},
          ),
          throwsA(
            isA<ApiException>()
                .having((e) => e.isFeatureOff, 'isFeatureOff', isTrue)
                .having((e) => e.feature, 'feature', FeatureKey.dataExport),
          ),
        );
        expect(asked, 1);
      },
    );
  });
}

/// The server's answer for a switched-off feature.
class _Refusing implements HttpClientAdapter {
  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<List<int>>? requestStream,
    Future<void>? cancelFuture,
  ) async => ResponseBody.fromString(
    '{"title":"Funksiya əlçatan deyil","detail":"Bu funksiya hazırda əlçatan deyil.",'
    '"status":404,"code":"features.unavailable","feature":"data_export"}',
    404,
    headers: {
      Headers.contentTypeHeader: ['application/problem+json'],
    },
  );

  @override
  void close({bool force = false}) {}
}

class _NoStorage implements SecureStorage {
  const _NoStorage();

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}
