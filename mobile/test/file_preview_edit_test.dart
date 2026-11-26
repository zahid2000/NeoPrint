import 'dart:typed_data';

import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/features/feature_flags.dart';
import 'package:neoprint/core/l10n/app_delegates.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/files/file_models.dart';
import 'package:neoprint/features/files/file_preview_screen.dart';
import 'package:neoprint/features/files/files_controller.dart';
import 'package:neoprint/features/files/office_editor_screen.dart';
import 'package:neoprint/features/print_hub/print_next.dart';
import 'package:neoprint/l10n/app_localizations.dart';

import 'office_editor_test.dart' show FixedFlags, OfficeRepository, office;
import 'print_flow_test.dart' show onePixelPng;

/// Files with pages, and Office sessions.
class PreviewRepository extends OfficeRepository {
  PreviewRepository(super.files);

  @override
  Future<Uint8List> pageImage(String id, int page, int width) async =>
      Uint8List.fromList(onePixelPng);

  @override
  Future<String> currentText(String id) async => 'Salam';
}

const allOn = FeatureFlags({
  FeatureKey.officeEditor: true,
  FeatureKey.txtEditor: true,
});

Widget app(
  UserFile file,
  PreviewRepository repository, {
  Map<String, bool> flags = const {
    FeatureKey.officeEditor: true,
    FeatureKey.txtEditor: true,
  },
}) => ProviderScope(
  overrides: [
    filesRepositoryProvider.overrideWithValue(repository),
    userHubEventsProvider.overrideWithValue(const Stream.empty()),
    featureFlagsProvider.overrideWith(() => FixedFlags(flags)),
    printNextRouteProvider.overrideWithValue(null),
    officeEditorViewProvider.overrideWithValue((
      page,
      session, {
      required onLoading,
      required onFailed,
    }) {
      WidgetsBinding.instance.addPostFrameCallback((_) => onLoading(false));
      return const Center(child: Text('Collabora'));
    }),
  ],
  child: MaterialApp(
    theme: AppTheme.light(),
    locale: const Locale('az'),
    localizationsDelegates: appLocalizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: FilePreviewScreen(file: file),
  ),
);

final edit = find.byKey(const ValueKey('filePreviewEdit'));

void main() {
  test('each format opens in its own editor, and some in none', () {
    expect(
      previewEditorFor(allOn, office('hesabat.docx')),
      PreviewEditor.office,
    );
    expect(
      previewEditorFor(allOn, office('cedvel.xlsx')),
      PreviewEditor.office,
    );
    expect(previewEditorFor(allOn, office('qeyd.txt')), PreviewEditor.text);
    expect(previewEditorFor(allOn, office('data.csv')), PreviewEditor.text);
    expect(previewEditorFor(allOn, office('hesabat.pdf')), isNull);
    expect(previewEditorFor(allOn, office('foto.jpg')), isNull);
    expect(previewEditorFor(allOn, office('makro.docm')), isNull);
    // A page edit made a PDF of the document.
    expect(
      previewEditorFor(allOn, office('hesabat.docx', current: 'pdf')),
      isNull,
    );
    // A saved scan is printed, never edited.
    expect(
      previewEditorFor(allOn, office('hesabat.docx', editLocked: true)),
      isNull,
    );

    // Still being prepared: the editor is known, the action waits.
    expect(
      previewEditorFor(allOn, office('hesabat.docx', status: 'Pending')),
      PreviewEditor.office,
    );

    // A flag that is off closes its editor alone.
    const officeOff = FeatureFlags({FeatureKey.officeEditor: false});
    expect(previewEditorFor(officeOff, office('hesabat.docx')), isNull);
    expect(previewEditorFor(officeOff, office('qeyd.txt')), PreviewEditor.text);
    const textOff = FeatureFlags({FeatureKey.txtEditor: false});
    expect(previewEditorFor(textOff, office('qeyd.txt')), isNull);
  });

  testWidgets('a document is edited from its preview', (tester) async {
    final file = office('hesabat.docx');
    final repository = PreviewRepository([file]);
    await tester.pumpWidget(app(file, repository));
    await tester.pumpAndSettle();

    expect(find.text('Redaktə et'), findsOneWidget);
    await tester.tap(edit);
    await tester.pumpAndSettle();

    expect(repository.sessions, ['hesabat.docx']);
    expect(find.text('Collabora'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a text file is edited from its preview', (tester) async {
    final file = office('qeyd.txt');
    await tester.pumpWidget(app(file, PreviewRepository([file])));
    await tester.pumpAndSettle();

    await tester.tap(edit);
    await tester.pumpAndSettle();

    expect(find.text('Mətn redaktoru'), findsOneWidget);
    expect(find.text('Salam'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a PDF has no edit action, and one being prepared waits', (
    tester,
  ) async {
    final pdf = office('hesabat.pdf');
    await tester.pumpWidget(app(pdf, PreviewRepository([pdf])));
    await tester.pumpAndSettle();
    expect(edit, findsNothing);

    final pending = office('hesabat.docx', status: 'Pending');
    await tester.pumpWidget(app(pending, PreviewRepository([pending])));
    await tester.pump();
    expect(edit, findsOneWidget);
    await tester.tap(edit);
    await tester.pump();
    expect(find.text('Collabora'), findsNothing);
    await tester.pumpWidget(const SizedBox());
  });
}
