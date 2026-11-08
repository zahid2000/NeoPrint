import 'dart:typed_data';

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
import 'package:neoprint/features/files/files_controller.dart';
import 'package:neoprint/features/print_hub/choose_file_screen.dart';
import 'package:neoprint/features/session/session_controller.dart';
import 'package:neoprint/features/session/session_models.dart';
import 'package:neoprint/l10n/app_localizations.dart';

import 'feature_flags_test.dart' show FixedFlags;
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

/// No open kiosk session.
class NoSession extends SessionController {
  @override
  Future<KioskSessionInfo?> build() async => null;
}

/// The Print tab's way from a file to its print settings (T-347), on a
/// router so the next screen can be seen.
Widget flow(
  List<UserFile> files, {
  required String start,
  Object? extra,
  Map<String, bool> flags = const {},
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
        path: Routes.scheduleNew,
        builder: (_, state) => const Text('schedule'),
      ),
    ],
  );

  return ProviderScope(
    overrides: [
      filesRepositoryProvider.overrideWithValue(PagesRepository(files)),
      userHubEventsProvider.overrideWithValue(const Stream.empty()),
      featureFlagsProvider.overrideWith(() => FixedFlags(flags)),
      sessionProvider.overrideWith(NoSession.new),
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

  testWidgets('with "Kiosk üçün hazırla" off the file goes on to a scheduled '
      'print', (tester) async {
    final ready = file();
    await tester.pumpWidget(
      flow(
        [ready],
        start: Routes.filePreview(ready.id),
        extra: ready,
        flags: {FeatureKey.userQrPrint: false},
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
}
