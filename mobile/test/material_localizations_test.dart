import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/l10n/material_localizations.dart';
import 'package:neoprint/l10n/app_localizations.dart';

/// T-156: the date picker names the weekdays (CLDR gives Azerbaijani 1–7).
void main() {
  Future<void> openPicker(WidgetTester tester, Locale locale) async {
    await tester.pumpWidget(
      MaterialApp(
        locale: locale,
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: const [
          AppLocalizations.delegate,
          AppMaterialLocalizationsDelegate(),
          GlobalMaterialLocalizations.delegate,
          GlobalWidgetsLocalizations.delegate,
          GlobalCupertinoLocalizations.delegate,
        ],
        home: Builder(
          builder: (context) => TextButton(
            onPressed: () => showDatePicker(
              context: context,
              initialDate: DateTime(2026, 10, 3),
              firstDate: DateTime(2026, 10, 1),
              lastDate: DateTime(2026, 11, 1),
            ),
            child: const Text('open'),
          ),
        ),
      ),
    );
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
  }

  testWidgets('Azerbaijani weekdays are names, Monday first', (tester) async {
    await openPicker(tester, const Locale('az'));

    for (final day in ['B.e.', 'Ç.a.', 'Ç.', 'C.a.', 'C.', 'Ş.', 'B.']) {
      expect(find.text(day), findsOneWidget, reason: day);
    }
    final monday = tester.getTopLeft(find.text('B.e.')).dx;
    final sunday = tester.getTopLeft(find.text('B.')).dx;
    expect(monday, lessThan(sunday));
    // The rest is still Flutter's own Azerbaijani.
    expect(find.text('oktyabr 2026'), findsOneWidget);
  });

  testWidgets('English uses the short names from the ARB file', (tester) async {
    await openPicker(tester, const Locale('en'));
    expect(find.text('Mo'), findsOneWidget);
    expect(find.text('Su'), findsOneWidget);
  });

  testWidgets('Russian uses the short names from the ARB file', (tester) async {
    await openPicker(tester, const Locale('ru'));
    expect(find.text('Пн'), findsOneWidget);
    expect(find.text('Вс'), findsOneWidget);
  });
}
