import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:neoprint/core/auth/auth_controller.dart';
import 'package:neoprint/core/auth/auth_models.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/router/app_links.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/files/file_models.dart';
import 'package:neoprint/features/payments/payment_models.dart';
import 'package:neoprint/features/payments/scan_screen.dart';
import 'package:neoprint/features/sharing/share_models.dart';
import 'package:neoprint/features/sharing/share_repository.dart';
import 'package:neoprint/features/sharing/share_send_screen.dart';
import 'package:neoprint/features/sharing/shares_screen.dart';
import 'package:neoprint/l10n/app_localizations.dart';

/// Sharing a file (plan section 8.2; docs/DESIGN_SYSTEM.md, "Mobil: fayl paylaşımı").
class FakeShares implements ShareRepository {
  FakeShares({this.receivedItems = const [], this.sentItems = const []});

  final List<ShareItem> receivedItems;
  final List<ShareItem> sentItems;
  final sends = <({String fileId, String to, String? message, bool confirm})>[];
  ApiException? refuseWith;

  @override
  Future<ShareItem> send({
    required String fileId,
    required String toPublicId,
    String? message,
    bool confirmSensitive = false,
  }) async {
    if (refuseWith case final error?) {
      throw error;
    }

    sends.add((
      fileId: fileId,
      to: PublicId.normalise(toPublicId),
      message: message,
      confirm: confirmSensitive,
    ));

    return share('s-new', ShareAvailability.available);
  }

  @override
  Future<List<ShareItem>> received() async => receivedItems;

  @override
  Future<List<ShareItem>> sent() async => sentItems;

  @override
  Future<Uint8List> myQr() async => Uint8List(0);

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

ShareItem share(
  String id,
  ShareAvailability availability, {
  String name = 'Vəsiqə.pdf',
  String? message,
}) => ShareItem(
  id: id,
  fileName: name,
  pageCount: 1,
  isSensitive: false,
  otherPublicId: 'NP-SEND01',
  createdAt: DateTime(2026, 10, 5, 9),
  availability: availability,
  message: message,
);

UserFile file({bool sensitive = false}) => UserFile.fromJson({
  'id': 'f1',
  'fileName': 'Vəsiqə.pdf',
  'extension': 'pdf',
  'source': 'Scan',
  'status': 'Ready',
  'sizeBytes': 1000,
  'pageCount': 1,
  'isSensitive': sensitive,
  'createdAt': '2026-10-05T09:00:00Z',
  'autoDelete': 'Never',
});

const me = UserProfile(
  id: 'u1',
  publicId: 'NP-ABC123',
  email: 'dev-user@neoprint.local',
  language: 'az',
  emailConfirmed: true,
);

List<Override> overrides(FakeShares shares) => [
  shareRepositoryProvider.overrideWithValue(shares),
  currentUserProvider.overrideWithValue(me),
  userHubEventsProvider.overrideWithValue(const Stream.empty()),
  canScanProvider.overrideWithValue(true),
];

/// The send screen on top of a home screen, as the files' menu opens it.
Widget sendApp(FakeShares shares, UserFile? sending) => ProviderScope(
  overrides: overrides(shares),
  child: MaterialApp.router(
    theme: AppTheme.light(),
    locale: const Locale('az'),
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    routerConfig: GoRouter(
      initialLocation: '/send',
      routes: [
        GoRoute(
          path: '/',
          builder: (_, __) => const Scaffold(body: Text('home')),
          routes: [
            GoRoute(
              path: 'send',
              builder: (_, __) => ShareSendScreen(file: sending),
            ),
          ],
        ),
      ],
    ),
  ),
);

void main() {
  test('an id is NP- and six letters or digits, in any case', () {
    expect(PublicId.normalise(' np-7kq 2mx '), 'NP-7KQ2MX');
    expect(PublicId.isValid('np-7kq2mx'), isTrue);
    expect(PublicId.isValid('7KQ2MX'), isFalse);
    expect(PublicId.isValid('NP-7KQ2M'), isFalse);
  });

  test('the share QR and the share links open the right screens', () {
    expect(
      ScannedCode.parse('neoprint://share/np-7kq2mx'),
      isA<ShareCode>().having((code) => code.publicId, 'id', 'NP-7KQ2MX'),
    );
    expect(ScannedCode.parse('neoprint://share/7KQ2MX'), isA<ForeignCode>());

    expect(
      appLinkLocation(Uri.parse('neoprint://share/np-7kq2mx')),
      '/shares/send?to=NP-7KQ2MX',
    );
    expect(appLinkLocation(Uri.parse('neoprint://shares')), '/shares');
  });

  test('a share reads its availability', () {
    final item = ShareItem.fromJson({
      'id': 's1',
      'fileName': 'a.pdf',
      'pageCount': 2,
      'isSensitive': true,
      'message': 'bax',
      'otherPublicId': 'NP-ABC123',
      'createdAt': '2026-10-05T09:00:00Z',
      'availability': 'Changed',
    });

    expect(item.availability, ShareAvailability.changed);
    expect((item.pageCount, item.isSensitive, item.message), (2, true, 'bax'));
  });

  testWidgets(
    'a personal document is sent only after the warning and the confirmation',
    (tester) async {
      final shares = FakeShares();
      await tester.pumpWidget(sendApp(shares, file(sensitive: true)));
      await tester.pumpAndSettle();

      expect(find.textContaining('Bu şəxsi sənəddir'), findsOneWidget);

      await tester.tap(find.text('Göndər'));
      await tester.pumpAndSettle();
      expect(find.text('Alıcının ID-sini yazın'), findsOneWidget);

      await tester.enterText(find.byType(TextField).first, 'np-7kq2mx');
      await tester.pump();
      expect(
        find.text('Alıcının ID-sini yazın'),
        findsNothing,
        reason: 'a fixed problem does not stay on screen',
      );
      expect(
        tester.widget<TextField>(find.byType(TextField).first).controller!.text,
        'NP-7KQ2MX',
        reason: 'an id is written in capitals',
      );

      await tester.tap(find.text('Göndər'));
      await tester.pumpAndSettle();

      expect(find.text('Şəxsi sənədi göndərək?'), findsOneWidget);
      expect(
        shares.sends,
        isEmpty,
        reason: 'nothing goes before the confirmation',
      );

      await tester.tap(
        find.descendant(
          of: find.byType(AlertDialog),
          matching: find.text('Göndər'),
        ),
      );
      await tester.pumpAndSettle();

      expect(shares.sends.single, (
        fileId: 'f1',
        to: 'NP-7KQ2MX',
        message: '',
        confirm: true,
      ));
      expect(
        find.text('Fayl NP-7KQ2MX istifadəçisinə göndərildi'),
        findsOneWidget,
      );
      expect(find.text('home'), findsOneWidget, reason: 'the screen closes');
    },
  );

  testWidgets('an unknown id is said under the field', (tester) async {
    final shares = FakeShares()
      ..refuseWith = const ApiException(
        statusCode: 404,
        message: 'Bu ID ilə istifadəçi tapılmadı, fayl göndərilmədi.',
        code: 'notFound.shareRecipient',
      );
    await tester.pumpWidget(sendApp(shares, file()));
    await tester.pumpAndSettle();

    await tester.enterText(find.byType(TextField).first, 'NP-000000');
    await tester.tap(find.text('Göndər'));
    await tester.pumpAndSettle();

    expect(
      find.text('Bu ID ilə istifadəçi tapılmadı, fayl göndərilmədi.'),
      findsOneWidget,
    );
    expect(
      find.text('Faylı paylaş'),
      findsOneWidget,
      reason: 'still on the screen to correct it',
    );
  });

  testWidgets('received files show their state, with the id to receive more', (
    tester,
  ) async {
    final shares = FakeShares(
      receivedItems: [
        share('s1', ShareAvailability.available, message: 'Salam'),
        share('s2', ShareAvailability.gone, name: 'Köhnə.pdf'),
        share('s3', ShareAvailability.changed, name: 'Dəyişən.pdf'),
      ],
    );

    await tester.pumpWidget(
      ProviderScope(
        overrides: overrides(shares),
        child: MaterialApp(
          theme: AppTheme.light(),
          locale: const Locale('az'),
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: const SharesScreen(),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('NP-ABC123'), findsOneWidget);
    expect(find.text('Yeni'), findsOneWidget);
    expect(find.text('Fayl artıq yoxdur'), findsOneWidget);
    expect(find.text('Dəyişdirilib'), findsOneWidget);
    expect(find.text('“Salam”'), findsOneWidget);

    await tester.tap(find.text('Göndərilənlər'));
    await tester.pumpAndSettle();

    expect(find.text('Hələ fayl göndərməmisiniz'), findsOneWidget);
  });
}
