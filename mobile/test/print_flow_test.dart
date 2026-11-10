import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:neoprint/core/features/feature_flags.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/router/app_router.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/files/file_models.dart';
import 'package:neoprint/features/files/file_preview_screen.dart';
import 'package:neoprint/features/files/cloud_picker.dart';
import 'package:neoprint/features/files/files_controller.dart';
import 'package:neoprint/features/kiosk_search/kiosk_models.dart';
import 'package:neoprint/features/kiosk_search/kiosk_search_controller.dart';
import 'package:neoprint/features/print_hub/choose_file_screen.dart';
import 'package:neoprint/features/print_hub/print_upload_screen.dart';
import 'package:neoprint/features/session/session_controller.dart';
import 'package:neoprint/features/session/session_models.dart';
import 'package:neoprint/l10n/app_localizations.dart';

import 'feature_flags_test.dart' show FixedFlags;
import 'kiosk_search_test.dart' show FakeKiosks, elmler, mayis;
import 'session_test.dart' show FakeSessions;
import 'files_screen_test.dart' show ListRepository, file;

/// The list, and pages that come back as a 1×1 PNG.
class PagesRepository extends ListRepository {
  PagesRepository(super.files);

  @override
  Future<Uint8List> pageImage(String id, int page, int width) async =>
      Uint8List.fromList(onePixelPng);
}

const onePixelPng = [
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
  0x06,
  0x00,
  0x00,
  0x00,
  0x1F,
  0x15,
  0xC4,
  0x89,
  0x00,
  0x00,
  0x00,
  0x0D,
  0x49,
  0x44,
  0x41,
  0x54,
  0x78,
  0x9C,
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
  0xA7,
  0x35,
  0x81,
  0x84,
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
];

/// An upload the server takes as 'Converting'; the next list has it ready
/// once [finish] is called.
class UploadingRepository extends PagesRepository {
  UploadingRepository() : super([]);

  @override
  Future<UserFile> upload({
    String? path,
    Uint8List? bytes,
    required String fileName,
    void Function(int sent, int total)? onProgress,
    CancelToken? cancelToken,
  }) async {
    final saved = file(name: fileName, status: 'Converting');
    files = [saved];
    return saved;
  }

  void finish() => files = [file(name: files.single.fileName)];
}

/// No open kiosk session.
class NoSession extends SessionController {
  @override
  Future<KioskSessionInfo?> build() async => null;
}

/// The Print tab's way from a file to its print settings (T-347), on a
/// router so the next screen can be seen.
/// Cloud apps on the phone; their chooser is closed without a file.
class FakeCloud implements CloudPicker {
  FakeCloud(this.apps);

  final List<CloudApp> apps;
  final opened = <CloudApp>[];

  @override
  Future<List<CloudApp>> installed() async => apps;

  @override
  Future<CloudFile?> pick(CloudApp app) async {
    opened.add(app);
    return null;
  }
}

Widget flow(
  List<UserFile> files, {
  required String start,
  Object? extra,
  Map<String, bool> flags = const {},
  ListRepository? repository,
  FakeSessions? sessions,
  List<KioskSummary> kiosks = const [elmler, mayis],
  CloudPicker? cloud,
}) {
  final router = GoRouter(
    initialLocation: start,
    initialExtra: extra,
    routes: [
      GoRoute(
        path: Routes.printChoose,
        builder: (_, __) => const ChooseFileScreen(),
      ),
      GoRoute(
        path: Routes.printUpload,
        builder: (_, state) =>
            PrintUploadScreen(file: state.extra! as PickedFile),
      ),
      GoRoute(
        path: '/file/:id',
        builder: (_, state) =>
            FilePreviewScreen(file: state.extra! as UserFile),
      ),
      GoRoute(
        path: Routes.prepare,
        builder: (_, state) =>
            Text('prepare ${(state.extra! as UserFile).fileName}'),
      ),
      GoRoute(
        path: Routes.sessionPrint,
        builder: (_, state) =>
            Text('print now ${(state.extra! as UserFile).fileName}'),
      ),
      GoRoute(
        path: Routes.scheduleNew,
        builder: (_, state) => const Text('schedule'),
      ),
    ],
  );

  return ProviderScope(
    overrides: [
      filesRepositoryProvider.overrideWithValue(
        repository ?? PagesRepository(files),
      ),
      userHubEventsProvider.overrideWithValue(const Stream.empty()),
      featureFlagsProvider.overrideWith(() => FixedFlags(flags)),
      sessionProvider.overrideWith(NoSession.new),
      sessionRepositoryProvider.overrideWithValue(sessions ?? FakeSessions()),
      kioskRepositoryProvider.overrideWithValue(FakeKiosks(kiosks)),
      if (cloud != null) cloudPickerProvider.overrideWithValue(cloud),
    ],
    child: MaterialApp.router(
      theme: AppTheme.light(),
      locale: const Locale('az'),
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      routerConfig: router,
    ),
  );
}

void main() {
  testWidgets('"Fayllarımdan seç" lists the ready files and a tap opens the '
      'file, whose one main button goes on to its print settings (T-347)', (
    tester,
  ) async {
    await tester.pumpWidget(
      flow([
        file(),
        file(name: 'yeni.docx', status: 'Converting'),
      ], start: Routes.printChoose),
    );
    await tester.pumpAndSettle();

    // Only what can be printed now.
    expect(find.text('hesabat.pdf'), findsOneWidget);
    expect(find.text('yeni.docx'), findsNothing);

    await tester.tap(find.text('hesabat.pdf'));
    await tester.pumpAndSettle();

    expect(find.text('Fayla baxış'), findsOneWidget);
    expect(find.text('3 səhifə · 1,5 KB · PDF'), findsOneWidget);
    await tester.scrollUntilVisible(
      find.text('Çap parametrləri'),
      100,
      scrollable: find.byType(Scrollable).first,
    );
    await tester.tap(find.text('Çap parametrləri'));
    await tester.pumpAndSettle();

    // T-376: no sheet - straight to the print settings.
    expect(find.text('Necə çap edək?'), findsNothing);
    expect(find.text('prepare hesabat.pdf'), findsOneWidget);

    // The file list keeps polling for the converting one; stop it.
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets(
    'a file still being prepared says so, and printing waits for it',
    (tester) async {
      final converting = file(name: 'yeni.docx', status: 'Converting');
      await tester.pumpWidget(
        flow(
          [converting],
          start: Routes.filePreview(converting.id),
          extra: converting,
        ),
      );
      await tester.pump();
      await tester.pump();

      expect(find.text('Fayl çapa hazırlanır'), findsOneWidget);
      final button = tester.widget<FilledButton>(find.byType(FilledButton));
      expect(button.onPressed, isNull);

      await tester.pumpWidget(const SizedBox());
    },
  );

  testWidgets('with printing now and "Kiosk üçün hazırla" off the file goes '
      'on to a scheduled print', (tester) async {
    final ready = file();
    await tester.pumpWidget(
      flow(
        [ready],
        start: Routes.filePreview(ready.id),
        extra: ready,
        flags: {
          FeatureKey.userQrPrint: false,
          FeatureKey.mobileSessionPrint: false,
        },
      ),
    );
    await tester.pumpAndSettle();

    await tester.scrollUntilVisible(
      find.text('Çap parametrləri'),
      100,
      scrollable: find.byType(Scrollable).first,
    );
    await tester.tap(find.text('Çap parametrləri'));
    await tester.pumpAndSettle();
    expect(find.text('schedule'), findsOneWidget);
  });

  testWidgets(
    'a file uploaded from the Print tab waits to be ready, then opens '
    'its print settings straight away (T-353)',
    (tester) async {
      final repository = UploadingRepository();
      await tester.pumpWidget(
        flow(
          const [],
          start: Routes.printUpload,
          extra: PickedFile(
            name: 'yeni.pdf',
            path: 'yeni.pdf',
            readBytes: () async => Uint8List(3),
          ),
          repository: repository,
          // Only the preparation is on: nothing to choose.
          flags: {FeatureKey.mobileSessionPrint: false},
        ),
      );
      await tester.pump();
      await tester.pump();

      // Uploaded; the server is still making its pages.
      expect(find.text('Hazırlanır'), findsOneWidget);
      expect(find.text('prepare yeni.pdf'), findsNothing);

      repository.finish();
      await tester.pump(FilesController.pollInterval);
      await tester.pumpAndSettle();

      // Not the preview: the print settings.
      expect(find.text('prepare yeni.pdf'), findsOneWidget);
      expect(find.text('Fayla baxış'), findsNothing);
    },
  );

  testWidgets('"Fayllarımdan seç" uploads a new file from the same sources '
      'as the files tab (T-353)', (tester) async {
    await tester.pumpWidget(flow([file()], start: Routes.printChoose));
    await tester.pumpAndSettle();

    await tester.tap(find.byType(FloatingActionButton));
    await tester.pumpAndSettle();

    expect(find.text('Fayl seç'), findsOneWidget);
    expect(find.text('Buluddan əlavə et'), findsOneWidget);
    expect(find.text('Şəkil çək'), findsOneWidget);
  });

  testWidgets('"Buluddan əlavə et" asks which cloud when there are several, '
      'and opens the chosen app chooser (T-399)', (tester) async {
    final cloud = FakeCloud([CloudApp.googleDrive, CloudApp.oneDrive]);
    await tester.pumpWidget(
      flow([file()], start: Routes.printChoose, cloud: cloud),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.byType(FloatingActionButton));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Buluddan əlavə et'));
    await tester.pumpAndSettle();

    expect(find.text('Hansı buluddan?'), findsOneWidget);
    await tester.tap(find.text('OneDrive'));
    await tester.pumpAndSettle();

    expect(cloud.opened, [CloudApp.oneDrive]);
  });

  testWidgets('one cloud app opens at once (T-399)', (tester) async {
    final cloud = FakeCloud([CloudApp.googleDrive]);
    await tester.pumpWidget(
      flow([file()], start: Routes.printChoose, cloud: cloud),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.byType(FloatingActionButton));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Buluddan əlavə et'));
    await tester.pumpAndSettle();

    expect(find.text('Hansı buluddan?'), findsNothing);
    expect(cloud.opened, [CloudApp.googleDrive]);
  });
}
