import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/shared/widgets/neo_design.dart';

/// T-398: a tabbed page is swiped sideways to the next or the previous tab;
/// a list on it still scrolls up and down.
void main() {
  Future<ValueNotifier<String>> pump(WidgetTester tester) async {
    final selected = ValueNotifier('a');
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ValueListenableBuilder<String>(
            valueListenable: selected,
            builder: (_, value, __) => NeoTabSwipe<String>(
              values: const ['a', 'b', 'c'],
              selected: value,
              onSelected: (next) => selected.value = next,
              child: ListView(
                children: [
                  for (var i = 0; i < 40; i++) ListTile(title: Text('$i')),
                ],
              ),
            ),
          ),
        ),
      ),
    );
    return selected;
  }

  testWidgets('right to left is the next tab, left to right the previous', (
    tester,
  ) async {
    final selected = await pump(tester);

    await tester.fling(find.byType(ListView), const Offset(-300, 0), 1000);
    await tester.pumpAndSettle();
    expect(selected.value, 'b');

    await tester.fling(find.byType(ListView), const Offset(-300, 0), 1000);
    await tester.fling(find.byType(ListView), const Offset(-300, 0), 1000);
    await tester.pumpAndSettle();
    expect(selected.value, 'c', reason: 'the last tab stays the last');

    await tester.fling(find.byType(ListView), const Offset(300, 0), 1000);
    await tester.pumpAndSettle();
    expect(selected.value, 'b');
  });

  testWidgets('scrolling the list does not change the tab', (tester) async {
    final selected = await pump(tester);

    await tester.fling(find.byType(ListView), const Offset(0, -300), 1000);
    await tester.pumpAndSettle();
    expect(selected.value, 'a');
    expect(find.text('0'), findsNothing, reason: 'the list scrolled');
  });

  double offsetOf(WidgetTester tester) => tester
      .widget<Transform>(
        find
            .descendant(
              of: find.byType(NeoTabSwipe<String>),
              matching: find.byType(Transform),
            )
            .first,
      )
      .transform
      .getTranslation()
      .x;

  testWidgets(
    'the page follows the finger and the next one slides in (T-415)',
    (tester) async {
      final selected = await pump(tester);
      final width = tester.getSize(find.byType(ListView)).width;

      // A little way and let go slowly: back it settles, no tab change.
      final short = await tester.startGesture(
        tester.getCenter(find.byType(ListView)),
      );
      await short.moveBy(const Offset(-30, 0));
      await short.moveBy(Offset(-width * 0.1, 0));
      await tester.pump();
      expect(offsetOf(tester), lessThan(0), reason: 'it follows the finger');
      await tester.pump(const Duration(seconds: 1));
      await short.up();
      await tester.pumpAndSettle();
      expect(selected.value, 'a');
      expect(offsetOf(tester), 0);

      // Over a quarter of the width, slowly: the next tab, sliding in.
      final long = await tester.startGesture(
        tester.getCenter(find.byType(ListView)),
      );
      await long.moveBy(const Offset(-30, 0));
      await long.moveBy(Offset(-width * 0.4, 0));
      await tester.pump(const Duration(seconds: 1));
      await long.up();
      await tester.pump();
      expect(selected.value, 'b');
      expect(offsetOf(tester), greaterThan(0), reason: 'in from the right');
      await tester.pump(const Duration(milliseconds: 120));
      expect(
        offsetOf(tester),
        inExclusiveRange(0, width * 0.3),
        reason: 'on its way',
      );
      await tester.pumpAndSettle();
      expect(offsetOf(tester), 0);
    },
  );

  testWidgets('past the last tab the page gives only a little (T-415)', (
    tester,
  ) async {
    final selected = await pump(tester);
    final width = tester.getSize(find.byType(ListView)).width;

    final drag = await tester.startGesture(
      tester.getCenter(find.byType(ListView)),
    );
    await drag.moveBy(const Offset(30, 0));
    await drag.moveBy(Offset(width * 0.4, 0));
    await tester.pump();
    expect(
      offsetOf(tester),
      lessThan(width * 0.15),
      reason: 'no tab before the first',
    );
    await drag.up();
    await tester.pumpAndSettle();
    expect(selected.value, 'a');
    expect(offsetOf(tester), 0);
  });

  testWidgets('a tab chosen by a tap slides in from its side (T-415)', (
    tester,
  ) async {
    final selected = await pump(tester);

    selected.value = 'c';
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 16));
    expect(
      offsetOf(tester),
      greaterThan(0),
      reason: 'a later tab, from the right',
    );
    await tester.pumpAndSettle();

    selected.value = 'a';
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 16));
    expect(
      offsetOf(tester),
      lessThan(0),
      reason: 'an earlier one, from the left',
    );
    await tester.pumpAndSettle();
    expect(offsetOf(tester), 0);
  });
}
