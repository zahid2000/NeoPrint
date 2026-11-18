import 'package:flutter_test/flutter_test.dart';
import 'package:image/image.dart' as img;
import 'package:material_ui/material_ui.dart';
import 'package:neoprint/core/l10n/app_delegates.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/l10n/app_localizations.dart';
import 'package:neoprint/shared/widgets/image_viewer.dart';

void main() {
  testWidgets('a picture opens full screen, swipes to the next and closes '
      '(T-446)', (tester) async {
    final picture = img.encodePng(img.Image(width: 8, height: 8));

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        locale: const Locale('az'),
        localizationsDelegates: appLocalizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Builder(
          builder: (context) => TextButton(
            onPressed: () => showImageViewer(
              context,
              images: [picture, picture],
              label: (n) => 'Şəkil $n',
            ),
            child: const Text('open'),
          ),
        ),
      ),
    );

    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    expect(find.text('1 / 2'), findsOneWidget);
    expect(find.bySemanticsLabel('Şəkil 1'), findsOneWidget);

    await tester.fling(find.byType(PageView), const Offset(-400, 0), 1000);
    await tester.pumpAndSettle();
    expect(find.text('2 / 2'), findsOneWidget);

    await tester.tap(find.byTooltip('Bağla'));
    await tester.pumpAndSettle();
    expect(find.byType(ImageViewer), findsNothing);
  });
}
