import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:image/image.dart' as img;
import 'package:neoprint/core/auth/auth_controller.dart';
import 'package:neoprint/core/auth/auth_models.dart';
import 'package:neoprint/core/platform/system_share.dart';
import 'package:neoprint/core/router/app_links.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/referral/referral_repository.dart';
import 'package:neoprint/features/referral/referral_screen.dart';
import 'package:neoprint/l10n/app_localizations.dart';

/// "Dostunu dəvət et" (plan section 2.6; docs/DESIGN_SYSTEM.md, "Mobil: dostunu dəvət et").
class FakeReferrals implements ReferralRepository {
  FakeReferrals(this.referral);

  final MyReferral referral;

  @override
  Future<MyReferral> mine() async => referral;

  @override
  Future<Uint8List> qr() async =>
      Uint8List.fromList(img.encodePng(img.Image(width: 8, height: 8)));
}

class FakeShare implements SystemShare {
  FakeShare({this.opens = true});

  final bool opens;
  final shared = <({String text, String? subject})>[];

  @override
  Future<bool> text(String text, {String? subject}) async {
    shared.add((text: text, subject: subject));
    return opens;
  }
}

MyReferral referral({int invited = 0, int rewarded = 0, int waiting = 0}) =>
    MyReferral(
      code: 'NPJQN9TM',
      link: 'https://neoprint.az/r/NPJQN9TM',
      invited: invited,
      rewarded: rewarded,
      waiting: waiting,
      bonusPagesEarned: rewarded * 10,
      bonusPagesPerInvite: 10,
      inviteeBonusPages: 3,
    );

const me = UserProfile(
  id: 'u1',
  publicId: 'NP-ABC123',
  email: 'dev-user@neoprint.local',
  language: 'az',
  emailConfirmed: true,
);

Widget app(MyReferral data, FakeShare share, {required bool canShare}) =>
    ProviderScope(
      overrides: [
        referralRepositoryProvider.overrideWithValue(FakeReferrals(data)),
        systemShareProvider.overrideWithValue(share),
        canShareProvider.overrideWithValue(canShare),
        currentUserProvider.overrideWithValue(me),
      ],
      child: MaterialApp(
        theme: AppTheme.light(),
        locale: const Locale('az'),
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const ReferralScreen(),
      ),
    );

/// A screen tall enough for the whole list: a lazy list builds only what shows.
void tall(WidgetTester tester) {
  tester.view.physicalSize = const Size(1080, 6600);
  tester.view.devicePixelRatio = 3;
  addTearDown(tester.view.reset);
}

void main() {
  /// What the app put on the clipboard.
  String? copied;

  setUp(() {
    copied = null;
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(SystemChannels.platform, (call) async {
          if (call.method == 'Clipboard.setData') {
            copied = (call.arguments as Map)['text'] as String?;
          }
          return null;
        });
  });

  tearDown(
    () => TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(SystemChannels.platform, null),
  );

  test('the numbers and the link are read as the server sends them', () {
    final parsed = MyReferral.fromJson({
      'code': 'NPJQN9TM',
      'link': 'https://neoprint.az/r/NPJQN9TM',
      'invited': 3,
      'rewarded': 1,
      'waiting': 2,
      'bonusPagesEarned': 10,
      'bonusPagesPerInvite': 10,
      'inviteeBonusPages': 0,
    });

    expect((parsed.invited, parsed.rewarded, parsed.waiting), (3, 1, 2));
    expect(appLinkLocation(Uri.parse('neoprint://referral')), '/referral');
    expect(
      appLinkLocation(Uri.parse('https://neoprint.az/r/npjqn9tm')),
      '/register?ref=NPJQN9TM',
      reason: 'the shared link opens registration with the code',
    );
  });

  testWidgets('the phone shares the code and the link through its own sheet', (
    tester,
  ) async {
    tall(tester);
    final share = FakeShare();
    await tester.pumpWidget(
      app(referral(invited: 3, rewarded: 1, waiting: 2), share, canShare: true),
    );
    await tester.pumpAndSettle();

    expect(find.text('NPJQN9TM'), findsOneWidget);
    expect(find.textContaining('10 bonus səhifə qazanırsınız'), findsOneWidget);
    expect(find.textContaining('Dostunuz da 3 bonus səhifə'), findsOneWidget);
    expect(find.text('Qazandığınız bonus: 10 səhifə'), findsOneWidget);

    await tester.tap(find.text('Paylaş'));
    await tester.pumpAndSettle();

    expect(share.shared.single.text, contains('NPJQN9TM'));
    expect(
      share.shared.single.text,
      contains('https://neoprint.az/r/NPJQN9TM'),
    );
    expect(copied, isNull, reason: 'the sheet opened, nothing else is done');
  });

  testWidgets('with no share sheet the link is copied, and said so', (
    tester,
  ) async {
    tall(tester);
    final share = FakeShare(opens: false);
    await tester.pumpWidget(app(referral(), share, canShare: true));
    await tester.pumpAndSettle();

    expect(find.textContaining('Hələ heç kim kodunuzla'), findsOneWidget);

    await tester.tap(find.text('Paylaş'));
    await tester.pumpAndSettle();

    expect(copied, 'https://neoprint.az/r/NPJQN9TM');
    expect(find.textContaining('link kopyalandı'), findsOneWidget);
  });

  testWidgets('the web build copies the link as its one main action', (
    tester,
  ) async {
    tall(tester);
    final share = FakeShare();
    await tester.pumpWidget(app(referral(), share, canShare: false));
    await tester.pumpAndSettle();

    expect(find.text('Paylaş'), findsNothing);
    expect(find.text('Linki kopyala'), findsOneWidget);

    await tester.tap(find.text('Linki kopyala'));
    await tester.pumpAndSettle();

    expect(copied, 'https://neoprint.az/r/NPJQN9TM');
    expect(find.text('Link kopyalandı'), findsOneWidget);
    expect(share.shared, isEmpty);
  });
}
