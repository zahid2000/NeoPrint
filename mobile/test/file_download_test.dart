import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/files/file_download.dart';
import 'package:neoprint/features/files/files_controller.dart';
import 'package:neoprint/features/files/files_screen.dart';
import 'package:neoprint/l10n/app_localizations.dart';

import 'files_screen_test.dart' show ListRepository, file;

class ContentRepository extends ListRepository {
  ContentRepository(super.files);

  @override
  Future<({Uint8List bytes, String mediaType})> content(String id) async =>
      (bytes: Uint8List.fromList([1, 2, 3]), mediaType: 'application/pdf');
}

class FakeSaver extends FileSaver {
  final saved = <(String, String, int)>[];

  @override
  bool get supported => true;

  @override
  Future<bool> save(String name, String mediaType, Uint8List bytes) async {
    saved.add((name, mediaType, bytes.length));
    return true;
  }
}

/// T-429: a file of the account saved onto the phone, where the user chooses.
void main() {
  testWidgets('"Cihaza yüklə" saves the file as uploaded and says so', (
    tester,
  ) async {
    final saver = FakeSaver();
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          filesRepositoryProvider.overrideWithValue(
            ContentRepository([file()]),
          ),
          userHubEventsProvider.overrideWithValue(const Stream.empty()),
          fileSaverProvider.overrideWithValue(saver),
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

    await tester.tap(find.byIcon(Icons.more_vert).first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Cihaza yüklə'));
    await tester.pumpAndSettle();

    expect(saver.saved, [('hesabat.pdf', 'application/pdf', 3)]);
    expect(find.text('Fayl telefona saxlanıldı.'), findsOneWidget);
  });
}
