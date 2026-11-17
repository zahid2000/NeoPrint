import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/contact/contact_screen.dart';
import 'package:neoprint/l10n/app_localizations.dart';

Widget app(List<ContactChannel> channels, Future<bool> Function(Uri) open) =>
    ProviderScope(
      overrides: [
        contactChannelsProvider.overrideWith((ref) async => channels),
        contactOpenerProvider.overrideWithValue(open),
      ],
      child: MaterialApp(
        theme: AppTheme.light(),
        locale: const Locale('az'),
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const ContactScreen(),
      ),
    );

void main() {
  test('only the four known places are read from the server (T-438)', () {
    expect(
      ContactChannel.fromJson({
        'kind': 'WhatsApp',
        'value': '+994 50 123 45 67',
        'link': 'https://wa.me/994501234567',
      })?.kind,
      ContactKind.whatsApp,
    );
    expect(
      ContactChannel.fromJson({
        'kind': 'Email',
        'value': 'x',
        'link': 'javascript:alert(1)',
      }),
      isNull,
    );
    expect(
      ContactChannel.fromJson({'kind': 'Fax', 'value': '1', 'link': 'tel:1'}),
      isNull,
    );
  });

  testWidgets('each active channel opens its own link; one that cannot be '
      'opened says what to type', (tester) async {
    final opened = <Uri>[];
    await tester.pumpWidget(
      app(
        [
          ContactChannel(
            kind: ContactKind.phone,
            value: '*1234',
            link: Uri.parse('tel:%2A1234'),
          ),
          ContactChannel(
            kind: ContactKind.telegram,
            value: '@neoprint_az',
            link: Uri.parse('https://t.me/neoprint_az'),
          ),
        ],
        (uri) async {
          opened.add(uri);
          return uri.scheme != 'tel';
        },
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Qaynar xətt'), findsOneWidget);
    expect(find.text('Telegram'), findsOneWidget);
    expect(find.text('WhatsApp'), findsNothing);

    await tester.tap(find.text('@neoprint_az'));
    await tester.pumpAndSettle();
    expect(opened.single, Uri.parse('https://t.me/neoprint_az'));

    await tester.tap(find.text('*1234'));
    await tester.pumpAndSettle();
    expect(
      find.text('Telefonda bunu açan tətbiq tapılmadı. Özünüz yazın: *1234'),
      findsOneWidget,
    );
  });

  testWidgets('none switched on: says so and points to "Müraciətlərim"', (
    tester,
  ) async {
    await tester.pumpWidget(app(const [], (_) async => true));
    await tester.pumpAndSettle();

    expect(find.text('Əlaqə kanalları hələ əlavə olunmayıb'), findsOneWidget);
    expect(find.text('Müraciətlərim'), findsOneWidget);
  });
}
