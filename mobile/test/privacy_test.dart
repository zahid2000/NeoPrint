import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/platform/browser.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/privacy/account_deletion.dart';
import 'package:neoprint/features/privacy/account_deletion_screen.dart';
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
  deletionTests();

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

class FakeDeletion implements AccountDeletionRepository {
  FakeDeletion(this.result);

  DeletionCheck result;
  final calls = <(String, String)>[];
  ApiException? refuse;

  @override
  Future<DeletionCheck> check() async => result;

  @override
  Future<void> delete({
    required String password,
    required String confirmation,
  }) async {
    calls.add((password, confirmation));
    if (refuse != null) {
      throw refuse!;
    }
  }
}

DeletionCheck deletionCheck({int scheduled = 0, double balance = 0}) =>
    DeletionCheck(
      activePrints: 0,
      scheduledPrints: scheduled,
      uncollectedOrders: 0,
      pendingRefunds: 0,
      balance: balance,
      bonusPages: balance > 0 ? 3 : 0,
    );

Widget deletionApp(FakeDeletion deletion, List<String> signedOut) =>
    ProviderScope(
      overrides: [
        accountDeletionRepositoryProvider.overrideWithValue(deletion),
        accountSignOutProvider.overrideWithValue(() async {
          signedOut.add('out');
        }),
      ],
      child: MaterialApp(
        theme: AppTheme.light(),
        locale: const Locale('az'),
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const AccountDeletionScreen(),
      ),
    );

void deletionTests() {
  group('Hesabımı sil (T-147)', () {
    testWidgets('a scheduled print stands in the way: no form, a way to it', (
      tester,
    ) async {
      await tester.pumpWidget(
        deletionApp(FakeDeletion(deletionCheck(scheduled: 1)), []),
      );
      await tester.pumpAndSettle();

      expect(find.text('Bu geri qaytarılmır'), findsOneWidget);
      expect(find.text('Planlanmış çap: 1'), findsOneWidget);
      expect(
        find.text('Hesabımı sil'),
        findsOneWidget,
        reason: 'the title only',
      );
      expect(find.byType(TextField), findsNothing);
    });

    testWidgets(
      'the balance is named; the button waits for the password and SİL; done signs out',
      (tester) async {
        final deletion = FakeDeletion(deletionCheck(balance: 2.5));
        final signedOut = <String>[];
        await tester.pumpWidget(deletionApp(deletion, signedOut));
        await tester.pumpAndSettle();

        // The money is written with a non-breaking space before the sign.
        expect(
          find.textContaining(
            RegExp(
              r'^Balansınızdakı 2,50.₼ və 3 bonus səhifə itəcək, geri qaytarılmır\.$',
            ),
          ),
          findsOneWidget,
        );
        FilledButton button() =>
            tester.widget<FilledButton>(find.byType(FilledButton));
        expect(button().onPressed, isNull);

        await tester.enterText(find.byType(TextField).first, 'Gizli!2026');
        await tester.enterText(find.byType(TextField).last, 'sil');
        await tester.pump();
        expect(button().onPressed, isNotNull, reason: 'any case of SİL');

        // A wrong password: said, and the account stays.
        deletion.refuse = const ApiException(
          statusCode: 400,
          message: 'Şifrə düz deyil.',
        );
        await tester.tap(find.byType(FilledButton));
        await tester.pumpAndSettle();
        expect(find.text('Şifrə düz deyil.'), findsOneWidget);
        expect(signedOut, isEmpty);

        deletion.refuse = null;
        await tester.tap(find.byType(FilledButton));
        // Signed out, the app leaves this screen; here the button keeps spinning.
        await tester.pump();
        await tester.pump(const Duration(seconds: 1));
        expect(deletion.calls.last, ('Gizli!2026', 'sil'));
        expect(signedOut, ['out']);
        expect(find.text('Hesabınız silindi.'), findsOneWidget);
      },
    );
  });
}
