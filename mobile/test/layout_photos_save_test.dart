import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:image/image.dart' as img;
import 'package:material_ui/material_ui.dart';
import 'package:neoprint/core/l10n/app_delegates.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/files/files_controller.dart';
import 'package:neoprint/features/layout/layout_editing.dart';
import 'package:neoprint/features/layout/layout_editor_screen.dart';
import 'package:neoprint/features/layout/layout_model.dart';
import 'package:neoprint/features/layout/layout_repository.dart';
import 'package:neoprint/l10n/app_localizations.dart';

import 'files_screen_test.dart' show ListRepository;
import 'photos_as_pdf_test.dart' show RecordingLayouts;

/// Photos opened in a layout (T-475): nothing is uploaded until the layout
/// is saved, and then the photos go with it - only the ones it still shows.
void main() {
  testWidgets('saving a layout of photos sends them with it, in one '
      'request', (tester) async {
    tester.view.physicalSize = const Size(1080, 2340);
    tester.view.devicePixelRatio = 3;
    addTearDown(tester.view.reset);

    final jpeg = Uint8List.fromList(
      img.encodeJpg(img.Image(width: 40, height: 60)),
    );
    const names = ['upload:photo-1.jpg', 'upload:photo-2.jpg'];
    final pictures = {
      for (final name in names)
        name: EditorPicture(name: name, aspect: 40 / 60),
    };
    // A page each; then the second page is taken away again.
    final project = LayoutEditing.onePerPage(
      const LayoutProject(marginMm: 10, pages: [LayoutPage([])]),
      0,
      [...pictures.values],
    );
    final shown = LayoutProject(
      marginMm: project.marginMm,
      pages: [project.pages.first],
    );
    final layouts = RecordingLayouts();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          filesRepositoryProvider.overrideWithValue(ListRepository([])),
          layoutRepositoryProvider.overrideWithValue(layouts),
          userHubEventsProvider.overrideWithValue(const Stream.empty()),
        ],
        child: MaterialApp(
          theme: AppTheme.light(),
          locale: const Locale('az'),
          localizationsDelegates: appLocalizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: LayoutEditorScreen(
            initial: shown,
            pictures: pictures,
            images: {for (final name in names) name: jpeg},
            mode: LayoutEditorMode.newFile,
          ),
        ),
      ),
    );
    await tester.runAsync(
      () => Future<void>.delayed(const Duration(milliseconds: 100)),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Yadda saxla'));
    await tester.pumpAndSettle();
    expect(find.text('Tərtibatın adı'), findsOneWidget);
    await tester.tap(find.text('Yadda saxla').last);
    await tester.pumpAndSettle();

    final (saved, _, photos) = layouts.created.single;
    expect(saved.pages.single.elements.single.image, names.first);
    expect(photos.keys, [names.first], reason: 'only what the layout shows');
  });
}
