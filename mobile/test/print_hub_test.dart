import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/features/feature_flags.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/print_hub/print_hub_screen.dart';
import 'package:neoprint/features/prepared/prepared_repository.dart';
import 'package:neoprint/features/schedule/schedule_repository.dart';
import 'package:neoprint/features/sharing/shares_controller.dart';
import 'package:neoprint/l10n/app_localizations.dart';

import 'feature_flags_test.dart' show FixedFlags;
import 'payments_kit.dart';
import 'prepared_test.dart' show FakePrepared;
import 'schedule_test.dart' show FakeSchedules;

Widget hub({bool canScan = true}) => ProviderScope(
  overrides: [
    ...step4Overrides(canScan: canScan),
    newSharesCountProvider.overrideWithValue(2),
    preparedRepositoryProvider.overrideWithValue(FakePrepared()),
    scheduleRepositoryProvider.overrideWithValue(FakeSchedules()),
    featureFlagsProvider.overrideWith(() => FixedFlags(const {})),
  ],
  child: MaterialApp(
    theme: AppTheme.light(),
    locale: const Locale('az'),
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: const PrintHubScreen(),
  ),
);

void main() {
  testWidgets('only ways to print, as equal rows - no upload here (T-338, '
      'T-353)', (tester) async {
    await tester.pumpWidget(hub());
    await tester.pumpAndSettle();

    expect(find.text('Yeni çap'), findsOneWidget);
    // No primary button: each row is one choice among equals.
    expect(find.byType(FilledButton), findsNothing);

    final rows = [
      'Fayllarımdan seç',
      'Paylaşılan fayllar',
      'Kioskdakı QR-ı oxut',
      'Kiosk seç',
      'Mənim QR kodum',
      'Hazırladıqlarım',
      'Planlı çap',
      // Every order as a history (T-386).
      'Sifarişlərim',
    ];
    for (final row in rows) {
      await tester.scrollUntilVisible(find.text(row), 200);
      expect(find.text(row), findsOneWidget, reason: row);
    }

    // A file is uploaded in "Fayllarımdan seç", not here (T-353).
    for (final upload in [
      'Telefondan fayl seç',
      'Buluddan əlavə et',
      'Şəkil çək',
      'Yeni tərtibat',
    ]) {
      expect(find.text(upload), findsNothing, reason: upload);
    }

    // The new shares are counted on their row.
    await tester.scrollUntilVisible(find.text('Paylaşılan fayllar'), -200);
    expect(find.text('2'), findsOneWidget);
  });

  testWidgets('a phone that cannot scan has no QR row', (tester) async {
    await tester.pumpWidget(hub(canScan: false));
    await tester.pumpAndSettle();
    await tester.scrollUntilVisible(find.text('Kiosk seç'), 200);

    expect(find.text('Kioskdakı QR-ı oxut'), findsNothing);
  });
}
