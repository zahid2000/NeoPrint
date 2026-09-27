import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/files/file_models.dart';
import 'package:neoprint/features/files/files_controller.dart';
import 'package:neoprint/features/files/files_repository.dart';
import 'package:neoprint/features/files/files_screen.dart';
import 'package:neoprint/l10n/app_localizations.dart';

/// A repository with a fixed list, for rendering the screen.
class ListRepository implements FilesRepository {
  ListRepository(this.files);

  final List<UserFile> files;

  @override
  Future<FilePage> list({int page = 1, int pageSize = 50}) async =>
      FilePage(items: files, total: files.length);

  @override
  Future<UserFile> upload({
    String? path,
    Uint8List? bytes,
    required String fileName,
    void Function(int sent, int total)? onProgress,
    CancelToken? cancelToken,
  }) => throw UnimplementedError();

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

Widget app(List<UserFile> files, {ThemeData? theme}) => ProviderScope(
  overrides: [filesRepositoryProvider.overrideWithValue(ListRepository(files))],
  child: MaterialApp(
    theme: theme ?? AppTheme.light(),
    locale: const Locale('az'),
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: const FilesScreen(),
  ),
);

UserFile file({
  String name = 'hesabat.pdf',
  String status = 'Ready',
  String source = 'Uploaded',
  bool sensitive = false,
  String? failure,
}) => UserFile.fromJson({
  'id': name,
  'fileName': name,
  'extension': extensionOf(name),
  'source': source,
  'status': status,
  'failureReason': failure,
  'sizeBytes': 1536,
  'pageCount': status == 'Ready' ? 3 : 0,
  'isSensitive': sensitive,
  'createdAt': DateTime.now().toUtc().toIso8601String(),
});

void main() {
  testWidgets('an empty list says so and offers the upload', (tester) async {
    await tester.pumpWidget(app(const []));
    await tester.pumpAndSettle();

    expect(find.text('Hələ faylınız yoxdur.'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Fayl yüklə'), findsOneWidget);
  });

  testWidgets('a file shows its size, pages, source and marks', (tester) async {
    await tester.pumpWidget(
      app([
        file(source: 'Scan', sensitive: true),
        file(name: 'yeni.docx', status: 'Converting'),
        file(
          name: 'pozuq.pdf',
          status: 'Failed',
          failure: 'Fayl şifrələnib, açmaq mümkün deyil.',
        ),
      ]),
    );
    await tester.pumpAndSettle();

    expect(find.text('hesabat.pdf'), findsOneWidget);
    expect(find.textContaining('1,5 KB · 3 səhifə · Bu gün'), findsOneWidget);
    expect(find.text('Skan'), findsOneWidget);
    expect(find.text('Həssas'), findsOneWidget);
    expect(find.text('Hazırlanır'), findsOneWidget);
    expect(find.text('Fayl şifrələnib, açmaq mümkün deyil.'), findsOneWidget);

    // One primary action on the screen: the upload button.
    expect(
      find.widgetWithText(FloatingActionButton, 'Fayl yüklə'),
      findsOneWidget,
    );

    // The list keeps polling while a file is being prepared; stop it.
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('the list renders in the dark theme too', (tester) async {
    await tester.pumpWidget(app([file()], theme: AppTheme.dark()));
    await tester.pumpAndSettle();

    expect(find.text('hesabat.pdf'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });
}
