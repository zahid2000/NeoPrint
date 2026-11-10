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
    Future<void> Function() act, {
    Future<LoadedPictures?> Function(BuildContext context)? onAdd,
  }) async {
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
                          onAddPictures: onAdd,
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

  // T-401: the first moves of a drag come before the parent has rebuilt with
  // the new selection; they belong to the picture touched, not to the one
  // selected before it.
  testWidgets('dragging another picture moves that one, not the one selected '
      'before', (tester) async {
    const first = LayoutElement(
      image: 'p1.jpg',
      xMm: 10,
      yMm: 10,
      widthMm: 50,
      heightMm: 50,
    );
    const second = LayoutElement(
      image: 'p1.jpg',
      xMm: 120,
      yMm: 200,
      widthMm: 50,
      heightMm: 50,
    );
    final changed = <int>[];

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: Align(
          alignment: Alignment.topLeft,
          child: SizedBox(
            width: 400,
            height: 600,
            child: LayoutCanvas(
              project: const LayoutProject(
                marginMm: 10,
                pages: [
                  LayoutPage([first, second]),
                ],
              ),
              page: 0,
              images: const {},
              // The parent still says the first one is selected.
              selected: 0,
              lockRatio: true,
              onSelect: (_) {},
              onChanged: (index, _, {required done}) => changed.add(index),
            ),
          ),
        ),
      ),
    );

    // A4 in 400 × 600: 1.714 px a millimetre from (32, 57.4).
    const scale = 360 / 210;
    final centre = const Offset(32, 57.43) + const Offset(145, 225) * scale;
    await tester.timedDragFrom(
      centre,
      const Offset(0, 40),
      const Duration(milliseconds: 300),
    );
    await tester.pumpAndSettle();

    expect(changed, isNotEmpty);
    expect(changed.toSet(), {1});
  });

  testWidgets('a file added in the editor comes onto the page (T-409)', (
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
        await tester.tap(find.byTooltip('Fayl əlavə et'));
        await tester.pumpAndSettle();
        await tester.tap(find.text('Hazır'));
        await tester.pumpAndSettle();
      },
      onAdd: (_) async => (
        {'p2.jpg': png},
        const {'p2.jpg': EditorPicture(name: 'p2.jpg', aspect: 0.5)},
      ),
    );

    expect(result!.pages.single.elements.map((e) => e.image), [
      'p1.jpg',
      'p2.jpg',
    ]);
  });

  testWidgets('several chosen at once are turned together; the page tools '
      'lay them out evenly (T-430)', (tester) async {
    const left = LayoutElement(
      image: 'p1.jpg',
      xMm: 20,
      yMm: 20,
      widthMm: 50,
      heightMm: 50,
    );
    const right = LayoutElement(
      image: 'p1.jpg',
      xMm: 120,
      yMm: 200,
      widthMm: 50,
      heightMm: 50,
    );
    final result = await openEditor(
      tester,
      const LayoutProject(
        marginMm: 10,
        pages: [
          LayoutPage([left, right]),
        ],
      ),
      () async {
        await tester.tap(find.text('Çoxlu seç'));
        await tester.pumpAndSettle();
        await tester.tap(find.text('Hamısını seç'));
        await tester.pumpAndSettle();
        await tester.tap(find.text('90°'));
        await tester.pumpAndSettle();
        await tester.tap(find.text('Seçimi bitir'));
        await tester.pumpAndSettle();
        await tester.tap(find.text('Hazır'));
        await tester.pumpAndSettle();
      },
    );

    expect(result!.pages.single.elements.map((e) => e.rotationDeg), [90, 90]);
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
