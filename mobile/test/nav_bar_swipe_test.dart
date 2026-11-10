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
}
