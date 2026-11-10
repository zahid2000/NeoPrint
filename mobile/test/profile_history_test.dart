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

  // T-393 - T-395: the code has a QR beside its copy button; no scheduled
  // prints here; the language, the PIN and privacy are one row away.
  testWidgets('the profile: a QR for the code, settings in one row', (
    tester,
  ) async {
    await tester.pumpWidget(
      screen(const ProfileScreen(), [...step4Overrides(), flags(const {})]),
    );
    await tester.pumpAndSettle();

    expect(find.byIcon(Icons.qr_code_2), findsOneWidget);
    expect(find.byIcon(Icons.copy_outlined), findsOneWidget);
    expect(find.text('Planlanmışlar'), findsNothing);
    expect(find.text('Dil'), findsNothing);
    expect(find.text('Hesabımı sil'), findsNothing);
    await tester.scrollUntilVisible(find.text('Tənzimləmələr'), 200);
    expect(find.text('Tənzimləmələr'), findsOneWidget);
  });
}
