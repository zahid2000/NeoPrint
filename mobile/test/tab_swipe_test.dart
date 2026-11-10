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
}
