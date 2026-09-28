import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:neoprint/core/auth/auth_controller.dart';
import 'package:neoprint/core/auth/auth_models.dart';
import 'package:neoprint/core/features/readiness.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/home/home_screen.dart';
import 'package:neoprint/features/profile/profile_screen.dart';
import 'package:neoprint/l10n/app_localizations.dart';
import 'package:neoprint/shared/widgets/bottom_nav_shell.dart';

/// A signed-in user without a name, so the fallbacks are on screen too.
const user = UserProfile(
  id: 'u1',
  publicId: 'NP-ABC123',
  email: 'dev-user@neoprint.local',
  language: 'az',
  emailConfirmed: true,
);

Widget app(Widget home) => ProviderScope(
  overrides: [currentUserProvider.overrideWithValue(user)],
  child: MaterialApp(
    theme: AppTheme.light(),
    locale: const Locale('az'),
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: home,
  ),
);

/// Nothing that looks broken: no dash standing in for a value, no button that
/// cannot be pressed, no list tile that cannot be tapped (Readiness).
void expectNothingUnfinished(WidgetTester tester) {
  expect(find.text('—'), findsNothing);

  for (final button in tester.widgetList<ButtonStyleButton>(
    find.byWidgetPredicate((widget) => widget is ButtonStyleButton),
  )) {
    expect(button.onPressed, isNotNull, reason: 'a disabled button');
  }

  for (final tile in tester.widgetList<ListTile>(find.byType(ListTile))) {
    expect(tile.enabled, isTrue, reason: 'a disabled row');
  }
}

void main() {
  testWidgets('the home tab shows only what works today', (tester) async {
    await tester.pumpWidget(app(const HomeScreen()));
    await tester.pumpAndSettle();

    expectNothingUnfinished(tester);
    expect(find.text('dev-user@neoprint.local'), findsOneWidget);
    expect(find.text('Fayllarım'), findsOneWidget);

    // The wallet and the scanner come in step 4; until then not even a label.
    expect(
      find.text('Balans'),
      Readiness.wallet ? findsOneWidget : findsNothing,
    );
    expect(
      find.text('QR oxut'),
      Readiness.kioskPayment ? findsOneWidget : findsNothing,
    );
  });

  testWidgets('the profile has no dash for a missing name', (tester) async {
    await tester.pumpWidget(app(const ProfileScreen()));
    await tester.pumpAndSettle();

    expectNothingUnfinished(tester);
    // The email stands in for the name, once.
    expect(find.text('dev-user@neoprint.local'), findsOneWidget);
  });

  testWidgets('the bottom bar lists only the tabs that have a screen', (
    tester,
  ) async {
    final router = GoRouter(
      initialLocation: '/a',
      routes: [
        StatefulShellRoute.indexedStack(
          builder: (context, state, shell) => BottomNavShell(shell: shell),
          branches: [
            for (final path in ['/a', '/b', '/c', '/d', '/e'])
              StatefulShellBranch(
                routes: [
                  GoRoute(path: path, builder: (_, __) => const SizedBox()),
                ],
              ),
          ],
        ),
      ],
    );

    await tester.pumpWidget(
      MaterialApp.router(
        theme: AppTheme.light(),
        locale: const Locale('az'),
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        routerConfig: router,
      ),
    );
    await tester.pumpAndSettle();

    final expected =
        3 +
        (Readiness.kioskSearch ? 1 : 0) +
        (Readiness.scheduledPrint ? 1 : 0);
    expect(find.byType(NavigationDestination), findsNWidgets(expected));
  });
}
