import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/features/profile/profile_screen.dart';

import 'feature_flags_test.dart' show flags, screen;
import 'payments_kit.dart';

void main() {
  // T-386: the orders, printed and waiting, are a row of the profile too -
  // not only behind the home screen's "Hamısına bax".
  testWidgets('the profile leads to every order', (tester) async {
    await tester.pumpWidget(
      screen(const ProfileScreen(), [...step4Overrides(), flags(const {})]),
    );
    await tester.pumpAndSettle();

    expect(find.text('Sifarişlərim'), findsOneWidget);
    expect(find.byIcon(Icons.history), findsOneWidget);
  });
}
