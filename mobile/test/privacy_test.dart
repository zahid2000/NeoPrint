import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/platform/browser.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/privacy/data_export.dart';
import 'package:neoprint/features/privacy/data_export_screen.dart';
import 'package:neoprint/l10n/app_localizations.dart';

/// The export API the test scripts.
class FakeDataExports implements DataExportRepository {
  FakeDataExports(this.current);

  DataExport? current;
  int requested = 0;

  @override
  Future<DataExport?> latest() async => current;

  @override
  Future<DataExport> request() async {
    requested++;
    return current = export(DataExportStatus.preparing);
  }
}

DataExport export(
  DataExportStatus status, {
  bool includesFiles = true,
  String? failure,
}) => DataExport(
  id: 'e1',
  status: status,
  requestedAt: DateTime.now(),
  readyAt: status == DataExportStatus.ready ? DateTime.now() : null,
  expiresAt: status == DataExportStatus.ready
      ? DateTime.now().add(const Duration(hours: 24))
      : null,
  sizeBytes: 2048,
  includesFiles: includesFiles,
  downloadUrl: status == DataExportStatus.ready
      ? 'api/exports/download?token=abc'
      : null,
  failureReason: failure,
);

final opened = <Uri>[];
var browserOpens = false;

Widget app(FakeDataExports exports) => ProviderScope(
  overrides: [
    browserProvider.overrideWithValue((url) async {
      opened.add(url);
      return browserOpens;
    }),
    dataExportRepositoryProvider.overrideWithValue(exports),
    userHubEventsProvider.overrideWithValue(const Stream.empty()),
  ],
  child: MaterialApp(
    theme: AppTheme.light(),
    locale: const Locale('az'),
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: const DataExportScreen(),
  ),
);

void main() {
  group('Məlumatlarımı yüklə (T-147)', () {
    testWidgets(
      'none yet: one button prepares it, then it says it is being made',
      (tester) async {
        final exports = FakeDataExports(null);
        await tester.pumpWidget(app(exports));
        await tester.pumpAndSettle();

        expect(find.textContaining('qəbzlər (PDF)'), findsOneWidget);
        await tester.tap(find.text('Arxiv hazırla'));
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 100));

        expect(exports.requested, 1);
        expect(
          find.text('Hazırlanır… Bitəndə bildiriş gələcək.'),
          findsOneWidget,
        );
        expect(find.text('Arxiv hazırla'), findsNothing);
      },
    );

    testWidgets(
      'ready: download, until when, and the files-only-listed warning',
      (tester) async {
        await tester.pumpWidget(
          app(
            FakeDataExports(
              export(DataExportStatus.ready, includesFiles: false),
            ),
          ),
        );
        await tester.pumpAndSettle();

        expect(find.text('Yüklə'), findsOneWidget);
        expect(find.textContaining('Son yükləmə vaxtı: Sabah'), findsNothing);
        expect(find.textContaining('Son yükləmə vaxtı:'), findsOneWidget);
        expect(find.textContaining('Ölçü: 2'), findsOneWidget);
        expect(find.textContaining('yalnız onların siyahısı'), findsOneWidget);

        // No browser on the phone: the user is told so.
        browserOpens = false;
        await tester.tap(find.text('Yüklə'));
        await tester.pumpAndSettle();
        expect(
          find.text('Link açılmadı. Telefonda brauzer olduğunu yoxlayın.'),
          findsOneWidget,
        );
        // The signed link, on the API's own address.
        expect(opened.last.path, endsWith('/api/exports/download'));
        expect(opened.last.queryParameters['token'], 'abc');
      },
    );

    testWidgets('failed: why, and to try again', (tester) async {
      final exports = FakeDataExports(
        export(
          DataExportStatus.failed,
          failure:
              'Arxivi hazırlamaq alınmadı. Bir az sonra yenidən cəhd edin.',
        ),
      );
      await tester.pumpWidget(app(exports));
      await tester.pumpAndSettle();

      expect(find.textContaining('Arxivi hazırlamaq alınmadı'), findsOneWidget);
      await tester.tap(find.text('Yenidən hazırla'));
      // Being prepared now: its spinner never settles.
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
      expect(exports.requested, 1);
    });
  });
}
