import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:neoprint/core/format/formats.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/files/file_models.dart';
import 'package:neoprint/features/kiosk_search/kiosk_search_controller.dart';
import 'package:neoprint/features/payments/payment_models.dart';
import 'package:neoprint/features/prepared/my_qr_screen.dart';
import 'package:neoprint/features/prepared/prepare_screen.dart';
import 'package:neoprint/features/prepared/prepared_list_screen.dart';
import 'package:neoprint/features/prepared/prepared_models.dart';
import 'package:neoprint/features/prepared/prepared_repository.dart';
import 'package:neoprint/features/session/session_controller.dart';
import 'package:neoprint/features/session/session_models.dart';
import 'package:neoprint/l10n/app_localizations.dart';

import 'kiosk_search_test.dart' show FakeKiosks, elmler;
import 'session_test.dart' show FakeSessions, detail;

/// The preparations API the test scripts.
class FakePrepared implements PreparedRepository {
  final prepared = <({String fileId, PreparedPreset preset})>[];
  final paid = <({String kiosk, double expected})>[];
  final cancelled = <String>[];
  final removed = <String>[];
  List<PreparedPrint> items = [];

  /// The kiosk's price now; a paid order with another amount is refused.
  double price = 0.30;
  Object? qrError;

  @override
  Future<List<PreparedPrint>> list() async => items;

  @override
  Future<PreparedPrint> prepare({
    required String fileId,
    required PreparedPreset preset,
    required String idempotencyKey,
  }) async {
    prepared.add((fileId: fileId, preset: preset));
    return PreparedPrint(
      isPaid: false,
      id: 'p1',
      fileId: fileId,
      fileName: 'hesabat.pdf',
      pages: 3,
      state: PreparedState.ready,
      createdAt: DateTime(2026, 10, 3),
      preset: preset,
    );
  }

  @override
  Future<void> remove(String id) async => removed.add(id);

  @override
  Future<PlacedOrder> preparePaid({
    required String kiosk,
    required String fileId,
    required OrderOptions options,
    required double expectedAmount,
    required String idempotencyKey,
  }) async {
    paid.add((kiosk: kiosk, expected: expectedAmount));

    if (toCents(expectedAmount) != toCents(price)) {
      throw const ApiException(
        statusCode: 409,
        message: 'Bu kioskun qiymətləri dəyişib.',
      );
    }

    return PlacedOrder(
      jobId: 'j1',
      jobCode: '123456',
      paymentId: 'pay1',
      amount: price,
    );
  }

  @override
  Future<void> cancelPaid(String jobId) async {
    cancelled.add(jobId);
    items = [];
  }

  @override
  Future<MyQr> qr() async {
    if (qrError case final error?) {
      throw error;
    }

    return MyQr(
      // A 1×1 white PNG.
      png: Uint8List.fromList(const [
        0x89,
        0x50,
        0x4E,
        0x47,
        0x0D,
        0x0A,
        0x1A,
        0x0A,
        0x00,
        0x00,
        0x00,
        0x0D,
        0x49,
        0x48,
        0x44,
        0x52,
        0x00,
        0x00,
        0x00,
        0x01,
        0x00,
        0x00,
        0x00,
        0x01,
        0x08,
        0x02,
        0x00,
        0x00,
        0x00,
        0x90,
        0x77,
        0x53,
        0xDE,
        0x00,
        0x00,
        0x00,
        0x0C,
        0x49,
        0x44,
        0x41,
        0x54,
        0x08,
        0xD7,
        0x63,
        0xF8,
        0xFF,
        0xFF,
        0x3F,
        0x00,
        0x05,
        0xFE,
        0x02,
        0xFE,
        0xDC,
        0xCC,
        0x59,
        0xE7,
        0x00,
        0x00,
        0x00,
        0x00,
        0x49,
        0x45,
        0x4E,
        0x44,
        0xAE,
        0x42,
        0x60,
        0x82,
      ]),
      expiresAt: DateTime.now().add(const Duration(minutes: 1)),
      rotationSeconds: 60,
    );
  }
}

/// The session fake's quote follows the prepared fake's price.
class PricedSessions extends FakeSessions {
  PricedSessions(this.prepared);

  final FakePrepared prepared;

  @override
  Future<OrderQuote> quote({
    required String kioskId,
    required OrderOptions options,
    required int pages,
  }) async => OrderQuote(
    total: prepared.price,
    sides: 3,
    sheets: 3,
    volumeDiscount: 0,
    discountPercent: 0,
  );
}

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

Widget app(Widget home, FakePrepared prepared) => ProviderScope(
  overrides: [
    preparedRepositoryProvider.overrideWithValue(prepared),
    sessionRepositoryProvider.overrideWithValue(PricedSessions(prepared)),
    kioskRepositoryProvider.overrideWithValue(
      FakeKiosks([elmler], details: {elmler.id: detail, elmler.code: detail}),
    ),
  ],
  child: MaterialApp.router(
    theme: AppTheme.light(),
    locale: const Locale('az'),
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    routerConfig: GoRouter(
      routes: [
        GoRoute(path: '/', builder: (_, __) => home),
        GoRoute(path: '/prepared', builder: (_, __) => const Text('prepared')),
        GoRoute(
          path: '/pay/:id',
          builder: (_, state) => Text('pay ${state.pathParameters['id']}'),
        ),
        GoRoute(path: '/my-qr', builder: (_, __) => const Text('my qr')),
      ],
    ),
  ),
);

String money(double amount) => formatMoney(amount);

void tallScreen(WidgetTester tester) {
  tester.view
    ..physicalSize = const Size(420, 2000)
    ..devicePixelRatio = 1;
  addTearDown(tester.view.reset);
}

void main() {
  group('prepare for a kiosk', () {
    testWidgets('without a kiosk only what was chosen is kept, unpaid', (
      tester,
    ) async {
      tallScreen(tester);
      final prepared = FakePrepared();
      await tester.pumpWidget(app(PrepareScreen(file: file), prepared));
      await tester.pumpAndSettle();

      // Every choice starts "at the kiosk".
      expect(find.text('Kioskda'), findsNWidgets(6));

      await tester.tap(find.text('Rəngli'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Hazırla'));
      await tester.pumpAndSettle();

      final saved = prepared.prepared.single;
      expect(saved.fileId, 'f1');
      expect(saved.preset.color, isTrue);
      expect(saved.preset.copies, isNull);
      expect(saved.preset.paper, isNull);
      expect(find.text('prepared'), findsOneWidget);
    });

    testWidgets(
      'with a kiosk every option is set, priced there and paid now; a changed price is asked again',
      (tester) async {
        tallScreen(tester);
        final prepared = FakePrepared();
        await tester.pumpWidget(app(PrepareScreen(file: file), prepared));
        await tester.pumpAndSettle();

        await tester.tap(find.text('Kiosk seç'));
        await tester.pumpAndSettle();
        await tester.tap(find.text('KSK-9002 · Elmlər'));
        await tester.pumpAndSettle();
        await tester.pump(const Duration(milliseconds: 400));
        await tester.pumpAndSettle();

        expect(find.text('Kioskda'), findsNothing);
        // This kiosk has no colour: the choice is there, with its reason.
        expect(find.text('Bu kiosk rəngli çap etmir.'), findsOneWidget);
        expect(find.text(money(0.30)), findsOneWidget);

        // The kiosk's prices change before paying.
        prepared.price = 0.40;
        await tester.tap(find.text('Əvvəlcədən ödə — ${money(0.30)}'));
        await tester.pumpAndSettle();

        expect(find.text('Qiymət dəyişib'), findsOneWidget);
        expect(
          find.text('Əvvəl: ${money(0.30)}. İndi: ${money(0.40)}.'),
          findsOneWidget,
        );
        await tester.tap(find.text('Yeni qiymətlə ödə'));
        await tester.pumpAndSettle();

        expect(prepared.paid.map((p) => p.expected), [0.30, 0.40]);
        expect(prepared.paid.last.kiosk, elmler.id);
        expect(find.text('pay pay1'), findsOneWidget);
      },
    );
  });

  group('prepared list', () {
    testWidgets(
      'a paid one says where and until when, and cancelling it says what comes back',
      (tester) async {
        tallScreen(tester);
        final prepared = FakePrepared()
          ..items = [
            PreparedPrint(
              isPaid: true,
              id: 'j1',
              fileId: 'f1',
              fileName: 'bilet.pdf',
              pages: 3,
              state: PreparedState.paid,
              createdAt: DateTime(2026, 10, 3),
              options: const OrderOptions(),
              kioskCode: 'KSK-9002',
              amount: 0.30,
              collectBy: DateTime(2026, 10, 10, 12),
            ),
          ];
        await tester.pumpWidget(app(const PreparedListScreen(), prepared));
        await tester.pumpAndSettle();

        expect(find.text('Ödənilib'), findsOneWidget);
        expect(
          find.text('KSK-9002 kioskunda · ${money(0.30)}'),
          findsOneWidget,
        );
        expect(find.textContaining('10.10.2026'), findsOneWidget);

        await tester.tap(find.byType(PopupMenuButton<String>));
        await tester.pumpAndSettle();
        await tester.tap(find.text('Ləğv et').last);
        await tester.pumpAndSettle();
        expect(
          find.text('${money(0.30)} balansınıza qaytarılacaq.'),
          findsOneWidget,
        );
        await tester.tap(find.text('Ləğv et').last);
        await tester.pumpAndSettle();

        expect(prepared.cancelled, ['j1']);
        expect(
          find.text('Kiosk üçün hazırladığınız fayl yoxdur'),
          findsOneWidget,
        );

        await tester.tap(find.text('Mənim QR kodum'));
        await tester.pumpAndSettle();
        expect(find.text('my qr'), findsOneWidget);
      },
    );
  });

  group('my QR', () {
    testWidgets('shows the code and says how often it changes', (tester) async {
      tallScreen(tester);
      await tester.pumpWidget(app(const MyQrScreen(), FakePrepared()));
      await tester.pumpAndSettle();

      expect(find.byType(Image), findsOneWidget);
      expect(find.text('Kod 60 saniyədən bir yenilənir'), findsOneWidget);
    });

    testWidgets('a code that could not be fetched is not shown', (
      tester,
    ) async {
      final prepared = FakePrepared()
        ..qrError = const ApiException.unreachable();
      await tester.pumpWidget(app(const MyQrScreen(), prepared));
      await tester.pumpAndSettle();

      expect(find.byType(Image), findsNothing);
      expect(find.textContaining('QR kodu yükləmək alınmadı'), findsOneWidget);
      expect(find.text('Yenidən cəhd et'), findsOneWidget);
    });
  });

  test('a preset sends only what was chosen', () {
    expect(PreparedPreset.none.toJson(), isEmpty);
    expect(const PreparedPreset(color: false, copies: 2).toJson(), {
      'copies': 2,
      'color': false,
    });
    final paid = PreparedPrint.fromJson({
      'kind': 'Paid',
      'id': 'j1',
      'fileId': 'f1',
      'fileName': 'bilet.pdf',
      'pages': 3,
      'state': 'Paid',
      'createdAt': '2026-10-03T08:00:00Z',
      'options': {'copies': 2, 'color': true, 'paper': 'A3'},
      'kioskCode': 'KSK-9002',
      'amount': 1.2,
      'collectBy': '2026-10-10T08:00:00Z',
    });
    expect(paid.isPaid, isTrue);
    expect(paid.options!.copies, 2);
    expect(paid.state, PreparedState.paid);
  });
}
