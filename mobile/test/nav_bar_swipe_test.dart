import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/shared/widgets/bottom_nav_shell.dart';

/// T-431: the bottom bar swiped sideways goes to the next or previous tab.
void main() {
  testWidgets('a swipe along the bar steps one tab, never past the ends', (
    tester,
  ) async {
    var index = 0;
    await tester.pumpWidget(
      MaterialApp(
        home: StatefulBuilder(
          builder: (context, setState) => Scaffold(
            bottomNavigationBar: NavBarSwipe(
              index: index,
              count: 3,
              onSelected: (next) => setState(() => index = next),
              child: const SizedBox(height: 80, width: 360),
            ),
          ),
        ),
      ),
    );
    final bar = find.byType(NavBarSwipe);

    await tester.fling(bar, const Offset(-200, 0), 1000);
    await tester.pumpAndSettle();
    expect(index, 1);

    await tester.drag(bar, const Offset(-20, 0));
    await tester.pumpAndSettle();
    expect(index, 1, reason: 'too short');

    await tester.fling(bar, const Offset(200, 0), 1000);
    await tester.fling(bar, const Offset(200, 0), 1000);
    await tester.pumpAndSettle();
    expect(index, 0, reason: 'the first stays the first');
  });

  testWidgets('the screen follows the finger and goes back on a short drag '
      '(T-432)', (tester) async {
    final moved = <double>[];
    var released = 0;
    var selected = -1;
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          bottomNavigationBar: NavBarSwipe(
            index: 1,
            count: 3,
            onSelected: (next) => selected = next,
            onDrag: moved.add,
            onRelease: () => released++,
            child: const SizedBox(height: 80, width: 360),
          ),
        ),
      ),
    );
    final bar = find.byType(NavBarSwipe);

    await tester.drag(bar, const Offset(-30, 0));
    await tester.pumpAndSettle();
    expect(moved, isNotEmpty);
    expect(moved.last, lessThan(0));
    expect(released, 1);
    expect(selected, -1);

    await tester.fling(bar, const Offset(-200, 0), 1000);
    await tester.pumpAndSettle();
    expect(selected, 2);
    expect(released, 1);
  });
}
