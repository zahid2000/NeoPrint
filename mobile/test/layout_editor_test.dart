import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:image/image.dart' as img;
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/layout/layout_canvas.dart';
import 'package:neoprint/features/layout/layout_editing.dart';
import 'package:neoprint/features/layout/layout_editor_screen.dart';
import 'package:neoprint/features/layout/layout_model.dart';
import 'package:neoprint/l10n/app_localizations.dart';

/// The layout editor on screen (plan section 11: "layout editor").
void main() {
  // A 100 × 100 mm picture in the middle of A4.
  const picture = LayoutElement(
    image: 'p1.jpg',
    xMm: 55,
    yMm: 98.5,
    widthMm: 100,
    heightMm: 100,
  );
  final png = img.encodePng(img.Image(width: 40, height: 40));

  Future<LayoutProject?> openEditor(
    WidgetTester tester,
    LayoutProject project,
    Future<void> Function() act,
  ) async {
    tester.view.physicalSize = const Size(1080, 2340);
    tester.view.devicePixelRatio = 3;
    addTearDown(tester.view.reset);

    LayoutProject? result;

    await tester.pumpWidget(
      ProviderScope(
        child: MaterialApp(
          theme: AppTheme.light(),
          locale: const Locale('az'),
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Builder(
            builder: (context) => Scaffold(
              body: TextButton(
                onPressed: () async =>
                    result = await Navigator.of(context).push<LayoutProject>(
                      MaterialPageRoute(
                        builder: (_) => LayoutEditorScreen(
                          initial: project,
                          pictures: const {
                            'p1.jpg': EditorPicture(name: 'p1.jpg', aspect: 1),
                          },
                          images: {'p1.jpg': png},
                          mode: LayoutEditorMode.scan,
                        ),
                      ),
                    ),
                child: const Text('open'),
              ),
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    await act();
    return result;
  }

  testWidgets('a picture is selected by a tap, turned and handed back', (
    tester,
  ) async {
    final result = await openEditor(
      tester,
      const LayoutProject(
        marginMm: 10,
        pages: [
          LayoutPage([picture]),
        ],
      ),
      () async {
        await tester.tap(find.byType(LayoutCanvas));
        await tester.pumpAndSettle();
        expect(find.text('100 × 100 mm'), findsOneWidget);

        await tester.tap(find.text('90°'));
        await tester.pumpAndSettle();
        await tester.tap(find.text('Hazır'));
        await tester.pumpAndSettle();
      },
    );

    expect(result!.pages.single.elements.single.rotationDeg, 90);
  });

  testWidgets('dragging moves the picture by the distance on paper', (
    tester,
  ) async {
    final result = await openEditor(
      tester,
      const LayoutProject(
        marginMm: 10,
        pages: [
          LayoutPage([picture]),
        ],
      ),
      () async {
        await tester.tap(find.byType(LayoutCanvas));
        await tester.pumpAndSettle();
        await tester.timedDrag(
          find.byType(LayoutCanvas),
          const Offset(0, 60),
          const Duration(milliseconds: 400),
        );
        await tester.pumpAndSettle();
        await tester.tap(find.text('Hazır'));
        await tester.pumpAndSettle();
      },
    );

    final moved = result!.pages.single.elements.single;
    expect(moved.yMm, greaterThan(picture.yMm + 10));
    expect(moved.xMm, picture.xMm, reason: 'a vertical drag keeps x');
  });

  testWidgets('a picture off the paper cannot be handed back', (tester) async {
    final result = await openEditor(
      tester,
      LayoutProject(
        marginMm: 10,
        pages: [
          LayoutPage([picture.copyWith(xMm: 250)]),
        ],
      ),
      () async {
        expect(find.textContaining('kağızdan kənardadır'), findsOneWidget);
        await tester.tap(find.text('Hazır'));
        await tester.pumpAndSettle();
      },
    );

    expect(result, isNull);
    expect(
      LayoutEditing.hasOffPage(
        LayoutProject(
          marginMm: 10,
          pages: [
            LayoutPage([picture.copyWith(xMm: 250)]),
          ],
        ),
      ),
      isTrue,
    );
  });
}
