import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/features/feature_flags.dart';
import 'package:neoprint/core/features/readiness.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/storage/secure_storage.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/files/files_controller.dart';
import 'package:neoprint/features/files/files_screen.dart';
import 'package:neoprint/features/prepared/prepared_models.dart';
import 'package:neoprint/features/prepared/prepared_repository.dart';
import 'package:neoprint/features/profile/profile_screen.dart';
import 'package:neoprint/features/schedule/schedule_models.dart';
import 'package:neoprint/features/session/session_controller.dart';
import 'package:neoprint/features/session/session_models.dart';
import 'package:neoprint/features/schedule/schedule_repository.dart';
import 'package:neoprint/l10n/app_localizations.dart';
import 'package:neoprint/l10n/app_localizations_az.dart';
import 'package:neoprint/shared/api_error_text.dart';
import 'package:neoprint/features/print_hub/print_hub_screen.dart';
import 'package:neoprint/features/sharing/shares_controller.dart';

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

  Future<void> openMenu(WidgetTester tester, {bool session = false}) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          filesRepositoryProvider.overrideWithValue(ListRepository([file])),
          userHubEventsProvider.overrideWithValue(const Stream.empty()),
          flags(const {}),
          sessionProvider.overrideWith(
            () => FixedSession(session ? openSession : null),
          ),
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
  }

  testWidgets('switched on, the file menu sends to print and plans it; '
      'without a kiosk session "Çapa göndər" is the preparation (T-354)', (
    tester,
  ) async {
    await openMenu(tester);

    expect(find.text('Çapa göndər'), findsOneWidget);
    // The same screen as "Çapa göndər": not offered twice.
    expect(find.text('Kiosk üçün hazırla'), findsNothing);
    expect(
      find.text('Çapı planla'),
      Readiness.scheduledPrint ? findsOneWidget : findsNothing,
    );
  });

  testWidgets('at a kiosk "Çapa göndər" prints there, and the preparation '
      'is offered beside it (T-354)', (tester) async {
    await openMenu(tester, session: true);

    expect(find.text('Çapa göndər'), findsOneWidget);
    expect(
      find.text('Kiosk üçün hazırla'),
      Readiness.personalQr ? findsOneWidget : findsNothing,
    );
  });

  testWidgets('the personal QR stays in the Print tab while a paid order '
      'waits to be collected', (tester) async {
    final prepared = FakePrepared();
    final overrides = [
      ...step4Overrides(),
      preparedRepositoryProvider.overrideWithValue(prepared),
      scheduleRepositoryProvider.overrideWithValue(FakeSchedules()),
      newSharesCountProvider.overrideWithValue(0),
      flags({FeatureKey.userQrPrint: false}),
    ];

    await tester.pumpWidget(screen(const PrintHubScreen(), overrides));
    await tester.pumpAndSettle();
    await tester.scrollUntilVisible(find.text('Kiosk seç'), 200);
    expect(find.text('Mənim QR kodum'), findsNothing);
    expect(find.text('Hazırladıqlarım'), findsNothing);

    prepared.items = [paidOrder()];
    await tester.pumpWidget(const SizedBox());
    await tester.pumpWidget(screen(const PrintHubScreen(), overrides));
    await tester.pumpAndSettle();
    await tester.scrollUntilVisible(find.text('Hazırladıqlarım'), 200);
    expect(find.text('Mənim QR kodum'), findsOneWidget);
    expect(find.text('Hazırladıqlarım'), findsOneWidget);
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

  // No longer a tab (T-338): "Planlı çap" is a row in the Print tab, and the
  // same rule decides it there, on the home screen and in the profile.
  group('scheduled print in the Print tab', () {
    Future<void> pumpHub(
      WidgetTester tester, {
      required List<ScheduledPrint> items,
      required bool on,
    }) async {
      await tester.pumpWidget(
        screen(const PrintHubScreen(), [
          ...step4Overrides(),
          newSharesCountProvider.overrideWithValue(0),
          preparedRepositoryProvider.overrideWithValue(FakePrepared()),
          scheduleRepositoryProvider.overrideWithValue(
            FakeSchedules()..items = items,
          ),
          flags({FeatureKey.scheduledPrint: on}),
        ]),
      );
      await tester.pumpAndSettle();
    }

    // The row is the last one: scrolled to the end, it is on screen if
    // it is there at all.
    Future<Finder> scheduledRow(WidgetTester tester) async {
      await tester.drag(find.byType(Scrollable).first, const Offset(0, -2000));
      await tester.pumpAndSettle();
      return find.text('Planlı çap');
    }

    testWidgets('is hidden when it is off and nothing is waiting', (
      tester,
    ) async {
      await pumpHub(tester, items: const [], on: false);

      expect(await scheduledRow(tester), findsNothing);
    }, skip: !Readiness.scheduledPrint);

    testWidgets('stays while a paid schedule is waiting', (tester) async {
      await pumpHub(
        tester,
        items: [schedule('s1', ScheduleStatus.scheduled)],
        on: false,
      );

      expect(await scheduledRow(tester), findsOneWidget);
    }, skip: !Readiness.scheduledPrint);
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

/// A kiosk session that is open, or none.
class FixedSession extends SessionController {
  FixedSession(this.session);

  final KioskSessionInfo? session;

  @override
  Future<KioskSessionInfo?> build() async => session;
}

final openSession = KioskSessionInfo(
  id: 's1',
  kioskId: 'k1',
  kioskCode: 'KSK-9002',
  kioskName: 'Elmlər',
  expiresAt: DateTime(2100),
  ttlMinutes: 10,
);
