import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/files/file_models.dart';
import 'package:neoprint/features/files/files_controller.dart';
import 'package:neoprint/features/layout/layout_launcher.dart';
import 'package:neoprint/l10n/app_localizations.dart';

import 'files_screen_test.dart' show ListRepository;
import 'flutter_test_config.dart';

UserFile document(String name, int pages) => UserFile.fromJson({
  'id': name,
  'fileName': name,
  'extension': extensionOf(name),
  'source': 'Uploaded',
  'status': 'Ready',
  'sizeBytes': 1536,
  'pageCount': pages,
  'isSensitive': false,
  'createdAt': testNow.toUtc().toIso8601String(),
  'autoDelete': 'Never',
});

/// T-416: a document's pages are chosen one by one, and every page chosen
/// goes into the layout - not only one.
void main() {
  Future<List<LayoutSource>? Function()> pump(
    WidgetTester tester,
    List<UserFile> files,
  ) async {
    tester.view
      ..physicalSize = const Size(400, 1600)
      ..devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    List<LayoutSource>? result;
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          filesRepositoryProvider.overrideWithValue(ListRepository(files)),
          userHubEventsProvider.overrideWithValue(const Stream.empty()),
        ],
        child: MaterialApp(
          theme: AppTheme.light(),
          locale: const Locale('az'),
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Builder(
            builder: (context) => TextButton(
              onPressed: () async => result = await Navigator.of(context)
                  .push<List<LayoutSource>>(
                    MaterialPageRoute(
                      builder: (_) => const LayoutSourcesScreen(),
                    ),
                  ),
              child: const Text('open'),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    return () => result;
  }

  testWidgets('every page chosen goes in, each one a picture', (tester) async {
    final result = await pump(tester, [document('hesabat.pdf', 5)]);

    await tester.tap(find.text('hesabat.pdf'));
    await tester.pumpAndSettle();
    expect(find.text('Səhifələr · 5 seçilib'), findsOneWidget);

    await tester.tap(find.widgetWithText(FilterChip, '2'));
    await tester.pumpAndSettle();
    expect(find.text('Səhifələr · 4 seçilib'), findsOneWidget);

    await tester.tap(find.text('Davam et'));
    await tester.pumpAndSettle();

    expect(result()!.map((source) => source.page), [1, 3, 4, 5]);
  });

  testWidgets('a long document starts with its first twelve pages; more than '
      'a layout takes is said, and not sent', (tester) async {
    final result = await pump(tester, [document('kitab.pdf', 30)]);

    await tester.tap(find.text('kitab.pdf'));
    await tester.pumpAndSettle();
    expect(find.text('Səhifələr · 12 seçilib'), findsOneWidget);

    await tester.tap(find.widgetWithText(FilterChip, 'Hamısı'));
    await tester.pumpAndSettle();
    expect(find.text('Səhifələr · 30 seçilib'), findsOneWidget);
    expect(find.textContaining('ən çox 24'), findsOneWidget);

    await tester.tap(find.text('Davam et'));
    await tester.pumpAndSettle();
    expect(result(), isNull, reason: 'the button is off');
  });
}
