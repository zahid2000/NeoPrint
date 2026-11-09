import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/shared/widgets/system_bar_frame.dart';

void main() {
  testWidgets(
    "a list with its own padding ends above the phone's navigation bar (T-352)",
    (tester) async {
      const navigationBar = 48.0;
      tester.view
        ..physicalSize = const Size(400, 800)
        ..devicePixelRatio = 1
        ..padding = const FakeViewPadding(bottom: navigationBar)
        ..viewPadding = const FakeViewPadding(bottom: navigationBar);
      addTearDown(tester.view.reset);

      await tester.pumpWidget(
        MaterialApp(
          theme: AppTheme.light(),
          builder: (context, child) => SystemBarFrame(child: child),
          home: Scaffold(
            body: ListView(
              // Explicit padding: ListView no longer adds the inset itself.
              padding: const EdgeInsets.all(16),
              children: [
                for (var i = 0; i < 40; i++)
                  SizedBox(height: 40, child: Text('line $i')),
              ],
            ),
          ),
        ),
      );

      await tester.drag(find.byType(ListView), const Offset(0, -5000));
      await tester.pumpAndSettle();

      final last = tester.getRect(find.text('line 39'));
      expect(last.bottom, lessThanOrEqualTo(800 - navigationBar));
    },
  );
}
