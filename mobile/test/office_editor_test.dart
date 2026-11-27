import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/auth/auth_controller.dart';
import 'package:neoprint/core/features/feature_flags.dart';
import 'package:neoprint/core/l10n/app_delegates.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/files/file_models.dart';
import 'package:neoprint/features/files/files_controller.dart';
import 'package:neoprint/features/files/files_screen.dart';
import 'package:neoprint/features/files/office_editor_screen.dart';
import 'package:neoprint/l10n/app_localizations.dart';

import 'files_screen_test.dart' show ListRepository, file;

/// The files, and Office editor sessions as the server hands them out.
class OfficeRepository extends ListRepository {
  OfficeRepository(super.files);

  final sessions = <String>[];

  /// The next answers: an exception is thrown, a session is returned.
  final answers = <Object>[];

  @override
  Future<OfficeSession> officeSession(String id) async {
    sessions.add(id);
    final answer = answers.isEmpty ? _session : answers.removeAt(0);
    if (answer is ApiException) {
      throw answer;
    }
    return answer as OfficeSession;
  }

  static const _session = OfficeSession(
    editorPath: '/browser/abc123/cool.html?WOPISrc=http%3A%2F%2Fapi%3A8080',
    accessToken: 'secret-token',
    accessTokenTtl: 1893456000000,
  );
}

class FixedFlags extends FeatureFlagsController {
  FixedFlags(this.values);

  final Map<String, bool> values;

  @override
  FeatureFlags build() => FeatureFlags(values);
}

/// Where the editor page would load, and with which token.
final opened = <(Uri, String)>[];

/// The editor's link to the app, as the screen handed it to the page.
OfficeEditorLink? lastLink;

/// How many times the app asked the editor to save.
var saveRequests = 0;

Widget app(OfficeRepository repository, {bool officeEditor = true}) =>
    ProviderScope(
      overrides: [
        filesRepositoryProvider.overrideWithValue(repository),
        userHubEventsProvider.overrideWithValue(const Stream.empty()),
        featureFlagsProvider.overrideWith(
          () => FixedFlags({FeatureKey.officeEditor: officeEditor}),
        ),
        officeEditorViewProvider.overrideWithValue((
          page,
          session, {
          required onLoading,
          required onFailed,
          required link,
        }) {
          opened.add((page, session.accessToken));
          lastLink = link
            ..save = () async {
              saveRequests++;
            };
          // The page loaded: the progress bar goes.
          WidgetsBinding.instance.addPostFrameCallback((_) => onLoading(false));
          return const Center(child: Text('Collabora'));
        }),
      ],
      child: MaterialApp(
        theme: AppTheme.light(),
        locale: const Locale('az'),
        localizationsDelegates: appLocalizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const FilesScreen(),
      ),
    );

UserFile office(
  String name, {
  String? current,
  String source = 'Uploaded',
  String status = 'Ready',
  bool editLocked = false,
}) => UserFile.fromJson({
  'id': name,
  'fileName': name,
  'extension': extensionOf(name),
  'currentExtension': current,
  'source': source,
  'status': status,
  'sizeBytes': 2048,
  'pageCount': 2,
  'isSensitive': false,
  'createdAt': DateTime.utc(2026, 11, 26).toIso8601String(),
  'editLocked': editLocked,
});

Future<void> openMenu(WidgetTester tester, int index) async {
  await tester.tap(find.byTooltip('Əməliyyatlar').at(index));
  await tester.pumpAndSettle();
}

void main() {
  setUp(() {
    opened.clear();
    lastLink = null;
    saveRequests = 0;
  });

  test('the page\'s messages reach the app, and nothing else does', () {
    final link = OfficeEditorLink();
    final saves = <bool>[];
    link.saves.stream.listen(saves.add);

    officeEditorMessage(link, '{"modified": true}');
    expect(link.modified.value, isTrue);
    officeEditorMessage(link, '{"modified": false}');
    expect(link.modified.value, isFalse);

    officeEditorMessage(link, 'not json');
    officeEditorMessage(link, '[1, 2]');
    officeEditorMessage(link, '{"modified": "yes"}');
    expect(link.modified.value, isFalse);

    officeEditorMessage(link, '{"saved": true}');
    return Future<void>.delayed(Duration.zero, () => expect(saves, [true]));
  });

  test('the app\'s page holds Collabora in a frame - it talks to its host '
      'only from one - and posts the token to it, not in a URL (T-472)', () {
    final page = officeHostPage(
      Uri.parse('https://test.example/browser/abc/cool.html?WOPISrc=x&lang=az'),
      const OfficeSession(
        editorPath: '/browser/abc/cool.html',
        accessToken: 'a"b<c>&d',
        accessTokenTtl: 1700000000000,
      ),
    );

    expect(
      page,
      contains(
        '<form id="open" action="https://test.example/browser/abc/cool.html'
        '?WOPISrc=x&amp;lang=az" method="post" target="editor">',
      ),
    );
    expect(page, contains('<iframe id="editor" name="editor"'));
    // The token is escaped, so it cannot break out of the attribute.
    expect(page, contains('value="a&quot;b&lt;c&gt;&amp;d"'));
    expect(page, isNot(contains('a"b<c>')));
    expect(page, contains('value="1700000000000"'));
    // Only the frame's messages, from the server's origin, are listened to;
    // Frame_Ready is answered with Host_PostmessageReady.
    expect(
      page,
      contains(
        'event.origin !== origin || event.source !== frame.contentWindow',
      ),
    );
    expect(page, contains("MessageId: 'Host_PostmessageReady'"));
    expect(page, contains('$officeEditorChannel.postMessage'));
    expect(officeSaveScript, contains('__neoprintSave'));
  });

  testWidgets('X closes at once while nothing has changed; a change brings '
      'the save icon, which saves', (tester) async {
    final repository = OfficeRepository([office('hesabat.docx')]);
    await tester.pumpWidget(app(repository));
    await tester.pumpAndSettle();
    await openMenu(tester, 0);
    await tester.tap(find.text('Redaktə et'));
    await tester.pumpAndSettle();

    // Typing in the document: Collabora says it is modified.
    lastLink!.modified.value = true;
    await tester.pump();
    expect(find.byKey(const ValueKey('officeEditorClose')), findsNothing);
    expect(find.byKey(const ValueKey('officeEditorSave')), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('officeEditorSave')));
    await tester.pump();
    expect(saveRequests, 1);

    // Collabora answers: saved.
    lastLink!.saves.add(true);
    await tester.pump();
    await tester.pump();
    expect(
      find.text('Saxlandı. Fayl yenidən çapa hazırlanır.'),
      findsOneWidget,
    );
    expect(find.byKey(const ValueKey('officeEditorClose')), findsOneWidget);

    // Nothing changed since: X leaves without asking.
    await tester.tap(find.byKey(const ValueKey('officeEditorClose')));
    await tester.pumpAndSettle();
    expect(find.text('Redaktordan çıxılsın?'), findsNothing);
    expect(find.text('Collabora'), findsNothing);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a save that fails says so and keeps the save icon', (
    tester,
  ) async {
    final repository = OfficeRepository([office('hesabat.docx')]);
    await tester.pumpWidget(app(repository));
    await tester.pumpAndSettle();
    await openMenu(tester, 0);
    await tester.tap(find.text('Redaktə et'));
    await tester.pumpAndSettle();

    lastLink!.modified.value = true;
    await tester.pump();
    await tester.tap(find.byKey(const ValueKey('officeEditorSave')));
    await tester.pump();
    lastLink!.saves.add(false);
    await tester.pump();
    await tester.pump();

    expect(
      find.text(
        'Saxlanmadı. Dəyişiklikləriniz redaktorda qalır — bir az sonra yenidən saxlayın.',
      ),
      findsOneWidget,
    );
    expect(find.byKey(const ValueKey('officeEditorSave')), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });

  test('only Word, Excel and PowerPoint documents open in the editor', () {
    expect(canEditOffice(office('hesabat.docx')), isTrue);
    expect(canEditOffice(office('cedvel.xlsx')), isTrue);
    expect(canEditOffice(office('slayd.pptx')), isTrue);
    expect(canEditOffice(office('metn.odt')), isTrue);

    // Macros are never carried through an edit.
    expect(canEditOffice(office('makro.docm')), isFalse);
    expect(canEditOffice(office('makro.xlsm')), isFalse);
    // Plain text has its own editor; a PDF is not a document.
    expect(canEditOffice(office('qeyd.txt')), isFalse);
    expect(canEditOffice(office('hesabat.pdf')), isFalse);
    // A page edit made a PDF of it.
    expect(canEditOffice(office('hesabat.docx', current: 'pdf')), isFalse);
    expect(canEditOffice(office('hesabat.docx', status: 'Pending')), isFalse);
    expect(canEditOffice(office('hesabat.docx', editLocked: true)), isFalse);
    expect(canEditOffice(office('hesabat.docx', source: 'Layout')), isFalse);
  });

  testWidgets('an older server without currentExtension falls back to the '
      'original one', (tester) async {
    expect(office('hesabat.docx').currentExtension, 'docx');
    expect(office('hesabat.docx', current: 'pdf').currentExtension, 'pdf');
  });

  testWidgets('"Redaktə et" is not offered while the flag is off', (
    tester,
  ) async {
    final repository = OfficeRepository([office('hesabat.docx')]);
    await tester.pumpWidget(app(repository, officeEditor: false));
    await tester.pumpAndSettle();

    await openMenu(tester, 0);

    expect(find.text('Redaktə et'), findsNothing);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a document opens in the editor with its token posted, not in '
      'the address, and leaving asks first', (tester) async {
    final repository = OfficeRepository([file(), office('hesabat.docx')]);
    await tester.pumpWidget(app(repository));
    await tester.pumpAndSettle();

    // A PDF has no Office editor.
    await openMenu(tester, 0);
    expect(find.text('Redaktə et'), findsNothing);
    await tester.tapAt(Offset.zero);
    await tester.pumpAndSettle();

    await openMenu(tester, 1);
    await tester.tap(find.text('Redaktə et'));
    await tester.pumpAndSettle();

    expect(repository.sessions, ['hesabat.docx']);
    expect(find.text('Collabora'), findsOneWidget);
    expect(find.text('hesabat.docx'), findsOneWidget);

    final (page, token) = opened.last;
    expect(
      page.toString(),
      startsWith(apiBaseUrl.replaceAll(RegExp(r'/$'), '')),
    );
    expect(page.path, '/browser/abc123/cool.html');
    expect(page.queryParameters['WOPISrc'], 'http://api:8080');
    expect(token, 'secret-token');
    expect(page.toString(), isNot(contains('secret-token')));

    // Closing asks; "Redaktəyə qayıt" stays.
    await tester.tap(find.byKey(const ValueKey('officeEditorClose')));
    await tester.pumpAndSettle();
    expect(find.text('Redaktordan çıxılsın?'), findsOneWidget);
    await tester.tap(find.text('Redaktəyə qayıt'));
    await tester.pumpAndSettle();
    expect(find.text('Collabora'), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('officeEditorClose')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('officeEditorLeave')));
    await tester.pumpAndSettle();

    expect(find.text('Collabora'), findsNothing);
    expect(find.text('hesabat.docx'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('an editor that is not running says so, and opens on a retry', (
    tester,
  ) async {
    final repository = OfficeRepository([office('hesabat.docx')])
      ..answers.add(
        const ApiException(
          statusCode: 409,
          message: 'Redaktor hazırda işləmir. Bir az sonra yenidən cəhd edin.',
        ),
      );
    await tester.pumpWidget(app(repository));
    await tester.pumpAndSettle();

    await openMenu(tester, 0);
    await tester.tap(find.text('Redaktə et'));
    await tester.pumpAndSettle();

    expect(
      find.text('Redaktor hazırda işləmir. Bir az sonra yenidən cəhd edin.'),
      findsOneWidget,
    );
    expect(opened, isEmpty);

    await tester.tap(find.byKey(const ValueKey('officeEditorRetry')));
    await tester.pumpAndSettle();

    expect(repository.sessions, ['hesabat.docx', 'hesabat.docx']);
    expect(find.text('Collabora'), findsOneWidget);

    // Nothing opened yet: closing does not ask.
    await tester.pumpWidget(const SizedBox());
  });
}
