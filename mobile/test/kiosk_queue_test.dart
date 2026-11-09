import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/kiosk_search/kiosk_queue_screen.dart';
import 'package:neoprint/features/kiosk_search/kiosk_search_controller.dart';
import 'package:neoprint/l10n/app_localizations.dart';

import 'kiosk_search_test.dart' show FakeKiosks;

/// A kiosk search whose queue the test sets.
class QueueKiosks extends FakeKiosks {
  QueueKiosks(this.rows) : super(const []);

  final List<QueueRow> rows;
  final asked = <String>[];

  @override
  Future<List<QueueRow>> queue(String kioskId) async {
    asked.add(kioskId);
    return rows;
  }
}

Widget app(QueueKiosks kiosks) => ProviderScope(
  overrides: [kioskRepositoryProvider.overrideWithValue(kiosks)],
  child: MaterialApp(
    theme: AppTheme.light(),
    locale: const Locale('az'),
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: const KioskQueueScreen(kioskId: 'k1', kioskName: 'Elmlər'),
  ),
);

void main() {
  // T-379: the poster's "Çap növbəsi" - whose an order is never, but the
  // user's own is marked with its code (a code cancels it at the kiosk).
  testWidgets('the queue row by row: the one printing first, the user\'s '
      'own marked with its code, the others without', (tester) async {
    final kiosks = QueueKiosks(const [
      QueueRow(
        position: 1,
        sides: 12,
        printedSides: 5,
        printing: true,
        isMine: false,
        readyInMinutes: 1,
      ),
      QueueRow(
        position: 2,
        sides: 28,
        printedSides: 0,
        printing: false,
        isMine: true,
        jobCode: '482913',
        readyInMinutes: 3,
      ),
    ]);
    await tester.pumpWidget(app(kiosks));
    await tester.pump();

    expect(kiosks.asked, ['k1']);
    expect(find.text('#1'), findsOneWidget);
    expect(find.text('5 / 12 səhifə'), findsOneWidget);
    expect(find.text('Sizin sifarişiniz · 482913'), findsOneWidget);
    expect(find.text('Sifariş'), findsOneWidget);
    expect(find.text('≈ 3 dəq'), findsOneWidget);

    // Its timer is let go.
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('an empty queue says so', (tester) async {
    await tester.pumpWidget(app(QueueKiosks(const [])));
    await tester.pump();

    expect(find.text('Növbə boşdur'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });
}
