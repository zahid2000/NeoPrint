import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart' show CancelToken;
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

UserFile layoutFile() => UserFile.fromJson({
  'id': 'layout',
  'fileName': 'Şəkillər.pdf',
  'extension': 'pdf',
  'source': 'Layout',
  'status': 'Pending',
  'sizeBytes': 100,
  'pageCount': 0,
  'isSensitive': false,
  'createdAt': DateTime.utc(2026, 11, 27).toIso8601String(),
});

/// Files that must not see a single upload: the photos are never files.
class NoUploads extends ListRepository {
  NoUploads() : super([]);

  var uploads = 0;

  @override
  Future<UserFile> upload({
    String? path,
    Uint8List? bytes,
    required String fileName,
    void Function(int sent, int total)? onProgress,
    CancelToken? cancelToken,
  }) async {
    uploads++;
    throw StateError('A photo was uploaded as a file.');
  }
}

class RecordingLayouts implements LayoutRepository {
  final created = <(LayoutProject, String, Map<String, Uint8List>)>[];

  @override
  Future<UserFile> createWithPhotos(
    LayoutProject layout,
    String fileName,
    Map<String, Uint8List> photos,
  ) async {
    created.add((layout, fileName, photos));
    return layoutFile();
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

/// A JPEG of [width] x [height] pixels, with EXIF [orientation] when given.
String photoFile(
  Directory dir,
  String name,
  int width,
  int height, {
  int? orientation,
}) {
  final image = img.Image(width: width, height: height);
  if (orientation != null) {
    image.exif.imageIfd.orientation = orientation;
  }
  final file = File('${dir.path}/$name')
    ..writeAsBytesSync(img.encodeJpg(image));
  return file.path;
}

void main() {
  test('a photo stored sideways comes out upright (T-473)', () {
    final image = img.Image(width: 400, height: 200)
      ..exif.imageIfd.orientation = 6;
    final upright = uprightJpeg(img.encodeJpg(image));

    expect(upright.aspect, 0.5);
    final decoded = img.decodeJpg(upright.bytes)!;
    expect((decoded.width, decoded.height), (200, 400));
  });

  testWidgets('several photos become one PDF in one request - a page each, '
      'upright - and none of them is ever a file (T-473)', (tester) async {
    final dir = Directory.systemTemp.createTempSync('photos');
    addTearDown(() => dir.deleteSync(recursive: true));
    final paths = [
      photoFile(dir, 'a.jpg', 400, 200, orientation: 6),
      photoFile(dir, 'b.jpg', 300, 300),
    ];
    final files = NoUploads();
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
                onPressed: () => savePhotosAsPdf(context, ref, paths),
                child: const Text('save'),
              ),
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.text('save'));
    await tester.runAsync(() async {
      for (var i = 0; i < 100 && layouts.created.isEmpty; i++) {
        await Future<void>.delayed(const Duration(milliseconds: 50));
        await tester.pump();
      }
    });
    await tester.pumpAndSettle();

    final (project, name, photos) = layouts.created.single;
    expect(name, startsWith('Şəkillər '));
    expect(project.pages, hasLength(2));
    expect(project.pages.map((page) => page.elements.single.image), [
      'upload:photo-1.jpg',
      'upload:photo-2.jpg',
    ]);
    expect(photos.keys, ['upload:photo-1.jpg', 'upload:photo-2.jpg']);

    // The sideways photo is framed upright: taller than wide, its own shape.
    final first = project.pages.first.elements.single;
    expect(first.widthMm / first.heightMm, closeTo(0.5, 0.01));
    final sent = img.decodeJpg(photos['upload:photo-1.jpg']!)!;
    expect((sent.width, sent.height), (200, 400));

    expect(files.uploads, 0);
    expect(find.text('PDF hazırdır — Fayllarımda.'), findsOneWidget);
  });
}
