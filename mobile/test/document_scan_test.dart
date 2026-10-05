import 'dart:io';

import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:image/image.dart' as img;
import 'package:neoprint/core/realtime/user_hub.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/files/file_models.dart';
import 'package:neoprint/features/files/files_controller.dart';
import 'package:neoprint/features/files/files_repository.dart';
import 'package:neoprint/features/files/files_screen.dart';
import 'package:neoprint/features/layout/layout_model.dart';
import 'package:neoprint/features/scanner/document_scan_screen.dart';
import 'package:neoprint/features/scanner/document_scanner.dart';
import 'package:neoprint/features/scanner/scanner_repository.dart';
import 'package:neoprint/l10n/app_localizations.dart';

/// The scanner hands back pages from the test's own folder.
class FakeScanner extends DocumentScanner {
  FakeScanner(this.pages);

  final List<String> pages;

  @override
  Future<List<String>> scan({int pageLimit = 10}) async => pages;
}

class FakeScannerRepository implements ScannerRepository {
  String? pending;
  final accepted = <String>[];
  final saves = <Map<String, Object?>>[];

  @override
  Future<String?> pendingNotice(String language) async => pending;

  @override
  Future<void> acceptNotice(String version) async => accepted.add(version);

  @override
  Future<UserFile> save({
    required Map<String, Uint8List> pages,
    required LayoutProject layout,
    required String documentType,
    double? widthMm,
    double? heightMm,
    required bool sensitive,
    required String fileName,
    void Function(int sent, int total)? onProgress,
  }) async {
    saves.add({
      'pages': pages.keys.toList(),
      'layout': layout,
      'documentType': documentType,
      'sensitive': sensitive,
      'fileName': fileName,
    });
    return UserFile.fromJson({
      'id': 'scan-1',
      'fileName': '$fileName.pdf',
      'extension': 'pdf',
      'source': 'Scan',
      'status': 'Pending',
      'createdAt': DateTime.utc(2030).toIso8601String(),
      'editLocked': true,
      'realSizePaper': 'A4',
    });
  }
}

class EmptyFiles implements FilesRepository {
  @override
  Future<FilePage> list({int page = 1, int pageSize = 50}) async =>
      const FilePage(items: [], total: 0);

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

Widget host(Widget home, FakeScannerRepository repository) => ProviderScope(
  overrides: [
    scannerRepositoryProvider.overrideWithValue(repository),
    filesRepositoryProvider.overrideWithValue(EmptyFiles()),
    userHubEventsProvider.overrideWithValue(const Stream<HubEvent>.empty()),
  ],
  child: MaterialApp(
    theme: AppTheme.light(),
    locale: const Locale('az'),
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: home,
  ),
);

/// A phone-sized screen, as the app is used.
void phone(WidgetTester tester) {
  tester.view.physicalSize = const Size(1080, 2340);
  tester.view.devicePixelRatio = 3;
  addTearDown(tester.view.reset);
}

void main() {
  late Directory folder;
  late List<String> pages;

  setUp(() {
    folder = Directory.systemTemp.createTempSync('neoprint-scan-');
    pages = [
      for (final name in ['front', 'back'])
        (File('${folder.path}/$name.jpg')..writeAsBytesSync(
              img.encodeJpg(img.Image(width: 1011, height: 638)),
            ))
            .path,
    ];
  });

  tearDown(() {
    if (folder.existsSync()) {
      folder.deleteSync(recursive: true);
    }
  });

  testWidgets('the scan option is in the upload sheet on a phone only', (
    tester,
  ) async {
    debugDefaultTargetPlatformOverride = TargetPlatform.android;
    await tester.pumpWidget(host(const FilesScreen(), FakeScannerRepository()));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Fayl yüklə'));
    await tester.pumpAndSettle();
    expect(find.text('Sənəd skanı'), findsOneWidget);
    debugDefaultTargetPlatformOverride = null;
  });

  testWidgets('a desktop build has no scanner in the upload sheet', (
    tester,
  ) async {
    debugDefaultTargetPlatformOverride = TargetPlatform.windows;
    await tester.pumpWidget(host(const FilesScreen(), FakeScannerRepository()));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Fayl yüklə'));
    await tester.pumpAndSettle();
    expect(find.text('Sənəd skanı'), findsNothing);
    debugDefaultTargetPlatformOverride = null;
  });

  testWidgets(
    'an ID card front and back is saved as one A4, always sensitive (flow 9.7)',
    (tester) async {
      final repository = FakeScannerRepository();
      phone(tester);

      await tester.runAsync(() async {
        await tester.pumpWidget(
          host(DocumentScanScreen(scanner: FakeScanner(pages)), repository),
        );
        // Reading the pages and making their previews happens off the test clock.
        for (var i = 0; i < 40; i++) {
          await Future<void>.delayed(const Duration(milliseconds: 100));
          await tester.pump();
          if (find.text('Səhifə 2').evaluate().isNotEmpty &&
              find.byType(CircularProgressIndicator).evaluate().isEmpty) {
            break;
          }
        }
      });

      expect(find.text('Səhifə 1'), findsOneWidget);
      expect(find.text('Səhifə 2'), findsOneWidget);

      // The ID card is the default; with two pages, front + back is offered first.
      await tester.scrollUntilVisible(
        find.byType(SwitchListTile),
        200,
        scrollable: find.byType(Scrollable).first,
      );
      final sensitive = tester.widget<SwitchListTile>(
        find.byType(SwitchListTile),
      );
      expect(sensitive.value, isTrue);
      expect(sensitive.onChanged, isNull);
      expect(find.text('Şəxsi sənəd — həmişə həssas'), findsOneWidget);

      await tester.scrollUntilVisible(
        find.text('Yadda saxla'),
        300,
        scrollable: find.byType(Scrollable).first,
      );

      await tester.runAsync(() async {
        await tester.tap(find.text('Yadda saxla'));
        for (var i = 0; i < 40 && repository.saves.isEmpty; i++) {
          await Future<void>.delayed(const Duration(milliseconds: 100));
          await tester.pump();
        }
      });

      expect(repository.saves, hasLength(1));
      final saved = repository.saves.single;
      expect(saved['documentType'], 'IdCard');
      expect(saved['sensitive'], isTrue);
      expect(saved['pages'], ['p1.jpg', 'p2.jpg']);
      final layout = saved['layout']! as LayoutProject;
      expect(layout.pages, hasLength(1));
      expect(layout.pages.single.elements.map((e) => e.image), [
        'p1.jpg',
        'p2.jpg',
      ]);
      expect(layout.pages.single.elements.first.widthMm, 85.6);

      // The scanner's pages are deleted once the scan is saved.
      await tester.runAsync(
        () => Future<void>.delayed(const Duration(milliseconds: 200)),
      );
      expect(pages.where((path) => File(path).existsSync()), isEmpty);
    },
  );

  testWidgets('the notice is asked first, and declining closes the scanner', (
    tester,
  ) async {
    final repository = FakeScannerRepository()..pending = '0.1-draft';
    phone(tester);

    await tester.pumpWidget(
      host(
        Builder(
          builder: (context) => Scaffold(
            body: TextButton(
              onPressed: () => Navigator.of(context).push(
                MaterialPageRoute<void>(
                  builder: (_) =>
                      DocumentScanScreen(scanner: FakeScanner(pages)),
                ),
              ),
              child: const Text('open'),
            ),
          ),
        ),
        repository,
      ),
    );
    await tester.tap(find.text('open'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 500));

    expect(find.text('Şəxsi sənədlərin skanı'), findsOneWidget);
    // Let the sheet finish sliding up before tapping it.
    await tester.pump(const Duration(milliseconds: 400));
    await tester.tap(find.text('İndi yox'));
    // The notice text is still loading behind: pump, do not settle.
    for (var i = 0; i < 5; i++) {
      await tester.pump(const Duration(milliseconds: 300));
    }

    expect(repository.accepted, isEmpty);
    expect(find.text('open'), findsOneWidget);
  });

  test('a scanner that is not there says so', () async {
    TestWidgetsFlutterBinding.ensureInitialized();
    const channel = MethodChannel(DocumentScanner.channelName);
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
          channel,
          (_) async => throw PlatformException(code: 'UNAVAILABLE'),
        );
    debugDefaultTargetPlatformOverride = TargetPlatform.android;

    await expectLater(
      const DocumentScanner().scan(),
      throwsA(
        isA<ScannerException>().having(
          (error) => error.reason,
          'reason',
          ScannerFailure.unavailable,
        ),
      ),
    );

    debugDefaultTargetPlatformOverride = null;
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, null);
  });
}
