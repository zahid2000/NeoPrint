import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:image/image.dart' as img;
import 'package:material_ui/material_ui.dart';
import 'package:neoprint/core/l10n/app_delegates.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/scanner/scan_models.dart';
import 'package:neoprint/features/scanner/scan_page_editor.dart';
import 'package:neoprint/features/scanner/scan_processing.dart';
import 'package:neoprint/l10n/app_localizations.dart';

/// The crop of a scanned page is four points (T-476): each corner moves on
/// its own, the middle of a side moves the side, and a shape whose sides
/// would cross is not taken.
void main() {
  Future<ScanPageEdit? Function()> open(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1080, 2340);
    tester.view.devicePixelRatio = 3;
    addTearDown(tester.view.reset);

    final dir = Directory.systemTemp.createTempSync('scan');
    addTearDown(() => dir.deleteSync(recursive: true));
    final file = File('${dir.path}/page.jpg')
      ..writeAsBytesSync(img.encodeJpg(img.Image(width: 300, height: 400)));

    ScanPageEdit? result;
    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        locale: const Locale('az'),
        localizationsDelegates: appLocalizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Builder(
          builder: (context) => TextButton(
            onPressed: () async => result = await editScanPage(
              context,
              ScanPage(path: file.path, width: 300, height: 400),
              1,
            ),
            child: const Text('open'),
          ),
        ),
      ),
    );
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    return () => result;
  }

  Future<void> drag(WidgetTester tester, String key, Offset by) async {
    final gesture = await tester.startGesture(
      tester.getCenter(find.byKey(ValueKey(key))),
    );
    // In steps, as a finger moves; the magnifier shows while it is held.
    for (var i = 0; i < 10; i++) {
      await gesture.moveBy(by / 10);
      await tester.pump();
    }
    expect(
      find.byType(RawMagnifier, skipOffstage: false),
      findsOneWidget,
      reason: key,
    );
    await gesture.up();
    await tester.pump();
    expect(find.byType(RawMagnifier), findsNothing);
  }

  testWidgets('a corner moves on its own; the others stay where they are', (
    tester,
  ) async {
    final result = await open(tester);

    await drag(tester, 'scanCropCorner-0', const Offset(40, 30));
    await tester.tap(find.text('Hazır'));
    await tester.pumpAndSettle();

    final crop = result()!.crop;
    expect(crop.topLeft.x, greaterThan(0.05));
    expect(crop.topLeft.y, greaterThan(0.03));
    expect(crop.topRight, (x: 1.0, y: 0.0));
    expect(crop.bottomRight, (x: 1.0, y: 1.0));
    expect(crop.bottomLeft, (x: 0.0, y: 1.0));
    expect(crop.isRect, isFalse);
  });

  testWidgets('the middle of a side moves that side; a corner dragged '
      'across the shape is not taken', (tester) async {
    final result = await open(tester);

    // The top side down.
    await drag(tester, 'scanCropEdge-0', const Offset(0, 50));
    // The top right corner dragged far past the bottom left: its sides
    // would cross, so it stops where the shape is still usable.
    await drag(tester, 'scanCropCorner-1', const Offset(-2000, 3000));
    await tester.tap(find.text('Hazır'));
    await tester.pumpAndSettle();

    final crop = result()!.crop;
    expect(crop.topLeft.y, greaterThan(0.05));
    expect(crop.topLeft.y, crop.topRight.y);
    expect(crop.bottomLeft, (x: 0.0, y: 1.0));
    expect(crop.isUsable, isTrue);
  });
}
