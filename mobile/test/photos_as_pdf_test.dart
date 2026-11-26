import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:image/image.dart' as img;
import 'package:material_ui/material_ui.dart';
import 'package:neoprint/core/l10n/app_delegates.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/files/file_models.dart';
import 'package:neoprint/features/files/files_controller.dart';
import 'package:neoprint/features/layout/layout_launcher.dart';
import 'package:neoprint/features/layout/layout_model.dart';
import 'package:neoprint/features/layout/layout_repository.dart';
import 'package:neoprint/l10n/app_localizations.dart';

import 'files_screen_test.dart' show ListRepository;

UserFile photo(String id, String status) => UserFile.fromJson({
  'id': id,
  'fileName': '$id.jpg',
  'extension': 'jpg',
  'source': 'Uploaded',
  'status': status,
  'sizeBytes': 100,
  'pageCount': status == 'Ready' ? 1 : 0,
  'isSensitive': false,
  'createdAt': DateTime.utc(2026, 11, 26).toIso8601String(),
});

/// Photos the server has made ready - one of them failed - and their pages.
class PhotoFiles extends ListRepository {
  PhotoFiles(super.files);

  final deleted = <String>[];
  final png = Uint8List.fromList(
    img.encodePng(img.Image(width: 30, height: 40)),
  );

  @override
  Future<Uint8List> pageImage(String id, int page, int width) async => png;

  @override
  Future<void> delete(String id) async {
    deleted.add(id);
    files = [...files.where((file) => file.id != id)];
  }
}

class RecordingLayouts implements LayoutRepository {
  final created = <(LayoutProject, String)>[];

  @override
  Future<UserFile> create(LayoutProject layout, String fileName) async {
    created.add((layout, fileName));
    return photo('layout', 'Pending');
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

void main() {
  testWidgets('several photos become one PDF layout, a page each, and the '
      'separate photos go; one that failed is named and left out (T-466)', (
    tester,
  ) async {
    final files = PhotoFiles([
      photo('p1', 'Ready'),
      photo('p2', 'Failed'),
      photo('p3', 'Ready'),
    ]);
    final layouts = RecordingLayouts();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          filesRepositoryProvider.overrideWithValue(files),
          layoutRepositoryProvider.overrideWithValue(layouts),
          userHubEventsProvider.overrideWithValue(const Stream.empty()),
        ],
        child: MaterialApp(
          theme: AppTheme.light(),
          locale: const Locale('az'),
          localizationsDelegates: appLocalizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(
            body: Consumer(
              builder: (context, ref, _) => TextButton(
                onPressed: () => savePhotosAsPdf(context, ref, files.files),
                child: const Text('save'),
              ),
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.text('save'));
    await tester.runAsync(() async {
      for (var i = 0; i < 20 && layouts.created.isEmpty; i++) {
        await Future<void>.delayed(const Duration(milliseconds: 50));
        await tester.pump();
      }
    });
    await tester.pumpAndSettle();

    final (project, name) = layouts.created.single;
    expect(project.pages, hasLength(2));
    expect(project.pages.map((page) => page.elements.single.image), [
      'file:p1',
      'file:p3',
    ]);
    expect(name, startsWith('Şəkillər '));

    // The layout keeps its own copies: the ready photos go.
    expect(files.deleted, ['p1', 'p3']);
    // The one the server could not prepare is named, not silently dropped.
    expect(find.textContaining('p2.jpg'), findsOneWidget);
    expect(files.deleted, isNot(contains('p2')));
  });
}
