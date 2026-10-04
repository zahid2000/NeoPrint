import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/realtime/user_hub.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/files/file_models.dart';
import 'package:neoprint/features/files/files_controller.dart';
import 'package:neoprint/features/files/files_repository.dart';
import 'package:neoprint/features/files/files_screen.dart';
import 'package:neoprint/l10n/app_localizations.dart';

/// A repository with a fixed list, for rendering the screen.
class ListRepository implements FilesRepository {
  ListRepository(this.files);

  List<UserFile> files;

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

  final rules = <(String, AutoDeleteRule)>[];

  @override
  Future<UserFile> setAutoDelete(String id, AutoDeleteRule rule) async {
    rules.add((id, rule));
    final current = files.firstWhere((f) => f.id == id);
    return UserFile.fromJson({
      'id': current.id,
      'fileName': current.fileName,
      'extension': current.extension,
      'source': 'Uploaded',
      'status': 'Ready',
      'sizeBytes': current.sizeBytes,
      'pageCount': current.pageCount,
      'isSensitive': current.isSensitive,
      'createdAt': current.createdAt.toUtc().toIso8601String(),
      'autoDelete': rule.wire,
    });
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

Widget app(
  List<UserFile> files, {
  ThemeData? theme,
  ListRepository? repository,
  Stream<HubEvent> hub = const Stream.empty(),
}) => ProviderScope(
  overrides: [
    filesRepositoryProvider.overrideWithValue(
      repository ?? ListRepository(files),
    ),
    userHubEventsProvider.overrideWithValue(hub),
  ],
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
  String autoDelete = 'Never',
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
  'autoDelete': autoDelete,
});

void main() {
  testWidgets('a file that deleted itself leaves the list at once (T-147)', (
    tester,
  ) async {
    final hub = StreamController<HubEvent>.broadcast();
    final repository = ListRepository([
      file(name: 'pasport.jpg', autoDelete: 'AfterPrint'),
      file(),
    ]);
    await tester.pumpWidget(
      app(const [], repository: repository, hub: hub.stream),
    );
    await tester.pumpAndSettle();
    expect(find.text('pasport.jpg'), findsOneWidget);

    repository.files = [file()];
    hub.add(
      const HubEvent('Notification', {
        'notificationId': 'n1',
        'type': 'FileAutoDeleted',
      }),
    );
    await tester.pumpAndSettle();

    expect(find.text('pasport.jpg'), findsNothing);
    expect(find.text('hesabat.pdf'), findsOneWidget);
    await hub.close();
  });

  testWidgets(
    'auto-delete is chosen from the file menu and shows on the card (T-147)',
    (tester) async {
      final repository = ListRepository([
        file(),
        file(name: 'pasport.jpg', sensitive: true, autoDelete: 'AfterPrint'),
      ]);
      await tester.pumpWidget(app(const [], repository: repository));
      await tester.pumpAndSettle();

      // The sensitive scan already deletes itself after printing.
      expect(find.text('Çapdan sonra silinəcək'), findsOneWidget);

      await tester.tap(find.byTooltip('Əməliyyatlar').first);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Avtomatik silinmə'));
      await tester.pumpAndSettle();

      expect(
        find.text(
          'Fayl bütün nüsxələri ilə birdəfəlik silinir, silinəndə bildiriş gəlir.',
        ),
        findsOneWidget,
      );
      expect(
        find.text('Bu faylla bütün sifarişlər çap olunandan sonra'),
        findsOneWidget,
      );

      await tester.tap(find.text('Yükləmədən 7 gün sonra'));
      await tester.pumpAndSettle();

      expect(repository.rules.single, (
        'hesabat.pdf',
        AutoDeleteRule.afterSevenDays,
      ));
      expect(find.text('7 gündən sonra silinəcək'), findsOneWidget);
    },
  );

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
