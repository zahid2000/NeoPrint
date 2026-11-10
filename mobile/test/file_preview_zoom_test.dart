import 'dart:async';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:image/image.dart' as img;
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/files/file_preview_screen.dart';
import 'package:neoprint/l10n/app_localizations.dart';

/// T-405: a page comes small at once and sharp after; the loads wait their
/// turn; the zoomed view has buttons to zoom and to move.
void main() {
  final png = Uint8List.fromList(
    img.encodePng(img.Image(width: 20, height: 28)),
  );

  test('no more than two loads run at once, in the order asked', () async {
    final queue = PageQueue();
    final gates = [for (var i = 0; i < 4; i++) Completer<void>()];
    final started = <int>[];

    final runs = [
      for (var i = 0; i < 4; i++)
        queue.run(() async {
          started.add(i);
          await gates[i].future;
          return i;
        }),
    ];
    await Future<void>.delayed(Duration.zero);
    expect(started, [0, 1]);

    gates[0].complete();
    await Future<void>.delayed(Duration.zero);
    await Future<void>.delayed(Duration.zero);
    expect(started, [0, 1, 2]);

    for (final gate in gates.skip(1)) {
      gate.complete();
    }
    expect(await Future.wait(runs), [0, 1, 2, 3]);
  });

  Future<List<int>> pump(WidgetTester tester) async {
    tester.view
      ..physicalSize = const Size(1080, 2340)
      ..devicePixelRatio = 3;
    addTearDown(tester.view.reset);

    final widths = <int>[];
    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        locale: const Locale('az'),
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: PagedPreview(
            pageCount: 2,
            load: (page, width) async {
              widths.add(width);
              return png;
            },
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    return widths;
  }

  testWidgets('the page comes small first, then sharp', (tester) async {
    final widths = await pump(tester);

    expect(widths.first, quickPageWidth);
    expect(widths, contains(1080), reason: 'the phone is 1080 pixels wide');
  });

  testWidgets('zoom in, move, fit: the buttons do what the fingers do', (
    tester,
  ) async {
    await pump(tester);

    IconButton button(String tooltip) => tester.widget<IconButton>(
      find.ancestor(
        of: find.byTooltip(tooltip),
        matching: find.byType(IconButton),
      ),
    );
    Matrix4 view() => tester
        .widget<InteractiveViewer>(find.byType(InteractiveViewer).first)
        .transformationController!
        .value;

    expect(button('Kiçilt').onPressed, isNull, reason: 'not zoomed yet');
    expect(button('Sağa').onPressed, isNull);

    await tester.tap(find.byTooltip('Böyüt'));
    await tester.pumpAndSettle();
    expect(view().getMaxScaleOnAxis(), closeTo(1.5, 0.01));
    expect(button('Sağa').onPressed, isNotNull);

    final before = view().getTranslation().x;
    await tester.tap(find.byTooltip('Sağa'));
    await tester.pumpAndSettle();
    expect(view().getTranslation().x, lessThan(before), reason: 'moved on');

    await tester.tap(find.byTooltip('Səhifəni sığdır'));
    await tester.pumpAndSettle();
    expect(view().getMaxScaleOnAxis(), 1);
    expect(button('Kiçilt').onPressed, isNull);
  });
}
