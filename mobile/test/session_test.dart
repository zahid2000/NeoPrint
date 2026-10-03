import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:neoprint/core/realtime/user_hub.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/files/file_models.dart';
import 'package:neoprint/features/kiosk_search/kiosk_detail_screen.dart';
import 'package:neoprint/features/kiosk_search/kiosk_models.dart';
import 'package:neoprint/features/kiosk_search/kiosk_search_controller.dart';
import 'package:neoprint/features/notices/hub_notices.dart';
import 'package:neoprint/features/payments/payment_models.dart';
import 'package:neoprint/features/session/print_options_screen.dart';
import 'package:neoprint/features/session/session_banner.dart';
import 'package:neoprint/features/session/session_controller.dart';
import 'package:neoprint/features/session/session_models.dart';
import 'package:neoprint/features/session/session_repository.dart';
import 'package:neoprint/l10n/app_localizations.dart';
import 'package:neoprint/l10n/app_localizations_az.dart';

import 'kiosk_search_test.dart' show FakeKiosks, elmler;

/// A session API the test scripts.
class FakeSessions implements SessionRepository {
  KioskSessionInfo? session;
  final opened = <String>[];
  final orders = <({String fileId, OrderOptions options, String key})>[];
  var closed = 0;
  OrderQuote quoteAnswer = const OrderQuote(
    total: 0.30,
    sides: 3,
    sheets: 3,
    volumeDiscount: 0,
    discountPercent: 0,
  );

  @override
  Future<KioskSessionInfo> open(String kiosk) async {
    opened.add(kiosk);
    return session = KioskSessionInfo(
      id: 's1',
      kioskId: 'k1',
      kioskCode: 'KSK-9002',
      kioskName: 'Elmlər',
      expiresAt: DateTime.now().add(const Duration(minutes: 10)),
      ttlMinutes: 10,
    );
  }

  @override
  Future<KioskSessionInfo?> current() async => session;

  @override
  Future<void> close(String sessionId) async {
    closed++;
    session = null;
  }

  @override
  Future<OrderQuote> quote({
    required String kioskId,
    required OrderOptions options,
    required int pages,
  }) async => quoteAnswer;

  @override
  Future<PlacedOrder> placeOrder({
    required String sessionId,
    required String fileId,
    required OrderOptions options,
    required String idempotencyKey,
  }) async {
    orders.add((fileId: fileId, options: options, key: idempotencyKey));
    return const PlacedOrder(
      jobId: 'j1',
      jobCode: '123456',
      paymentId: 'p1',
      amount: 0.30,
    );
  }
}

final hubEvents = StreamController<HubEvent>.broadcast();

List<Override> overrides(FakeSessions sessions, {FakeKiosks? kiosks}) => [
  sessionRepositoryProvider.overrideWithValue(sessions),
  kioskRepositoryProvider.overrideWithValue(
    kiosks ?? FakeKiosks([elmler], details: {'KSK-9002': detail, 'k1': detail}),
  ),
  userHubEventsProvider.overrideWithValue(hubEvents.stream),
  userHubReconnectedProvider.overrideWithValue(const Stream.empty()),
];

const detail = KioskDetail(
  summary: elmler,
  color: false,
  duplex: true,
  a3: false,
  paper: [KioskPaper(size: 'A4', state: PaperState.ok, sheets: 2)],
  queueLength: 0,
  prices: KioskPrices(
    bwPerSide: 0.1,
    colorPerSide: 0.3,
    paperMultipliers: {'A4': 1},
    minOrder: 0.2,
    tiers: [],
  ),
);

final file = UserFile(
  id: 'f1',
  fileName: 'hesabat.pdf',
  extension: 'pdf',
  source: FileSourceKind.uploaded,
  status: FileStatus.ready,
  sizeBytes: 1000,
  pageCount: 3,
  isSensitive: false,
  versionCount: 0,
  createdAt: DateTime(2026, 10, 3),
);

Widget app(Widget home, List<Override> overrides) => ProviderScope(
  overrides: overrides,
  child: MaterialApp.router(
    theme: AppTheme.light(),
    locale: const Locale('az'),
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    routerConfig: GoRouter(
      routes: [
        GoRoute(path: '/', builder: (_, __) => home),
        GoRoute(
          path: '/pay/:id',
          builder: (_, state) => Text('pay ${state.pathParameters['id']}'),
        ),
        GoRoute(
          path: '/session/files',
          builder: (_, __) => const Text('pick a file'),
        ),
      ],
    ),
  ),
);

/// All the options on one screen, as on a tall phone.
void tallScreen(WidgetTester tester) {
  tester.view
    ..physicalSize = const Size(420, 1600)
    ..devicePixelRatio = 1;
  addTearDown(tester.view.reset);
}

void main() {
  group('kiosk detail', () {
    testWidgets(
      'an online kiosk opens a session, then offers a file and closing it',
      (tester) async {
        final sessions = FakeSessions();
        await tester.pumpWidget(
          app(
            const KioskDetailScreen(idOrCode: 'KSK-9002'),
            overrides(sessions),
          ),
        );
        await tester.pumpAndSettle();

        await tester.tap(find.text('Sessiya aç'));
        await tester.pumpAndSettle();
        expect(sessions.opened, ['KSK-9002']);

        expect(find.text('Fayl seç'), findsOneWidget);
        await tester.tap(find.text('Sessiyanı bağla'));
        await tester.pumpAndSettle();
        expect(sessions.closed, 1);
        expect(find.text('Sessiya aç'), findsOneWidget);
      },
    );

    testWidgets('a kiosk not taking orders says so instead of a button', (
      tester,
    ) async {
      final offline = KioskDetail(
        summary: const KioskSummary(
          id: 'k1',
          code: 'KSK-9002',
          name: 'Elmlər',
          address: '',
          lat: 0,
          lng: 0,
          status: KioskStatus.offline,
        ),
        color: false,
        duplex: false,
        a3: false,
        paper: const [],
        queueLength: 0,
      );
      await tester.pumpWidget(
        app(
          const KioskDetailScreen(idOrCode: 'KSK-9002'),
          overrides(
            FakeSessions(),
            kiosks: FakeKiosks([], details: {'KSK-9002': offline}),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Sessiya aç'), findsNothing);
      expect(
        find.textContaining('Bu kiosk hazırda sifariş qəbul etmir'),
        findsOneWidget,
      );
    });
  });

  group('banner', () {
    testWidgets(
      'shows the open session and goes when the hub says it expired',
      (tester) async {
        final sessions = FakeSessions();
        await sessions.open('KSK-9002');
        await tester.pumpWidget(
          app(
            const Scaffold(bottomNavigationBar: SessionBanner()),
            overrides(sessions),
          ),
        );
        await tester.pumpAndSettle();

        expect(find.text('KSK-9002 · Elmlər'), findsOneWidget);
        expect(find.textContaining('dəq qalıb'), findsOneWidget);

        hubEvents.add(
          const HubEvent('SessionClosed', {
            'sessionId': 's1',
            'kioskId': 'k1',
            'reason': 'Expired',
          }),
        );
        await tester.pumpAndSettle();
        expect(find.text('KSK-9002 · Elmlər'), findsNothing);
      },
    );
  });

  group('print options', () {
    testWidgets(
      'a kiosk without colour keeps the choice with its reason, and the order goes to payment',
      (tester) async {
        tallScreen(tester);
        final sessions = FakeSessions();
        await sessions.open('KSK-9002');
        await tester.pumpWidget(
          app(PrintOptionsScreen(file: file), overrides(sessions)),
        );
        await tester.pumpAndSettle();

        expect(find.text('Bu kiosk rəngli çap etmir.'), findsOneWidget);
        expect(find.text('Bu kiosk A3 çap etmir.'), findsOneWidget);
        expect(find.text('0,30 ₼'), findsOneWidget);
        expect(find.text('3 üz · 3 vərəq'), findsOneWidget);

        // Three sheets, two in the tray: said before ordering.
        expect(
          find.textContaining('kifayət qədər kağız yoxdur'),
          findsOneWidget,
        );

        await tester.tap(find.byTooltip('Nüsxəni artır'));
        await tester.pumpAndSettle();
        await tester.tap(find.text('Sifariş et — 0,30 ₼'));
        await tester.pumpAndSettle();

        expect(sessions.orders.single.fileId, 'f1');
        expect(sessions.orders.single.options.copies, 2);
        expect(find.text('pay p1'), findsOneWidget);
      },
    );

    testWidgets(
      'a range that does not fit says how to write it and orders nothing',
      (tester) async {
        tallScreen(tester);
        final sessions = FakeSessions();
        await sessions.open('KSK-9002');
        await tester.pumpWidget(
          app(PrintOptionsScreen(file: file), overrides(sessions)),
        );
        await tester.pumpAndSettle();

        await tester.tap(find.text('Aralıq'));
        await tester.pumpAndSettle();
        await tester.enterText(find.byType(TextField), '2-9');
        await tester.pumpAndSettle();

        await tester.tap(find.text('Sifariş et'));
        await tester.pumpAndSettle();

        expect(sessions.orders, isEmpty);
        expect(find.textContaining('1–3 arası səhifələri'), findsWidgets);
      },
    );
  });

  test('a page range reads as the server reads it', () {
    expect(parsePageRange(null, 3), [1, 2, 3]);
    expect(parsePageRange('1-2, 2, 3', 3), [1, 2, 3]);
    expect(parsePageRange('3,1', 3), [3, 1]);
    expect(parsePageRange('0-2', 3), isNull);
    expect(parsePageRange('2-1', 3), isNull);
    expect(parsePageRange('+2', 3), isNull);
    expect(parsePageRange('1,,2', 3), isNull);
  });

  test("the kiosk's QR opens the kiosk; a bank QR is not ours", () {
    expect(
      ScannedCode.parse('neoprint://kiosk/ksk-9002'),
      isA<KioskCode>().having((c) => c.code, 'code', 'KSK-9002'),
    );
    expect(ScannedCode.parse('MOCKBANK:123'), isA<ForeignCode>());
  });

  test('only an expired session is announced', () {
    final l10n = AppLocalizationsAz();
    expect(
      noticeFor(
        l10n,
        const HubEvent('SessionClosed', {'reason': 'Expired'}),
        'az',
      ),
      l10n.noticeSessionExpired,
    );
    expect(
      noticeFor(
        l10n,
        const HubEvent('SessionClosed', {'reason': 'Closed'}),
        'az',
      ),
      isNull,
    );
  });
}
