import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/core/theme/tokens.dart';
import 'package:neoprint/shared/widgets/neo_banner.dart';
import 'package:neoprint/shared/widgets/neo_primary_button.dart';
import 'package:neoprint/shared/widgets/neo_status_chip.dart';

void main() {
  group('theme', () {
    test('both themes carry the tokens', () {
      expect(
        AppTheme.light().extension<NeoPrintTokens>(),
        same(NeoPrintTokens.light),
      );
      expect(
        AppTheme.dark().extension<NeoPrintTokens>(),
        same(NeoPrintTokens.dark),
      );
    });

    test('the two themes really differ', () {
      // Catches the mistake of wiring both to the same palette, which looks
      // fine in the light theme and unreadable in the dark one.
      expect(
        NeoPrintTokens.light.neutral0,
        isNot(NeoPrintTokens.dark.neutral0),
      );
      expect(
        NeoPrintTokens.light.neutral900,
        isNot(NeoPrintTokens.dark.neutral900),
      );
    });

    test('a primary button clears the minimum touch target', () {
      final size = AppTheme.light().filledButtonTheme.style?.minimumSize
          ?.resolve({});

      expect(size, isNotNull);
      expect(size!.height, greaterThanOrEqualTo(Sizes.touchTarget));
    });

    test('the label always sits above the field, never inside it', () {
      // A placeholder that doubles as the label disappears as soon as there
      // is text (docs/DESIGN_SYSTEM.md section 4).
      expect(
        AppTheme.light().inputDecorationTheme.floatingLabelBehavior,
        FloatingLabelBehavior.always,
      );
    });

    test('every tone has a foreground and a background that differ', () {
      for (final theme in [NeoPrintTokens.light, NeoPrintTokens.dark]) {
        for (final tone in StatusTone.values) {
          final colours = theme.forTone(tone);

          expect(
            colours.foreground,
            isNot(colours.background),
            reason: 'tone $tone',
          );
        }
      }
    });
  });

  group('shared widgets', () {
    Widget wrap(Widget child, {Brightness brightness = Brightness.light}) {
      return MaterialApp(
        theme: brightness == Brightness.light
            ? AppTheme.light()
            : AppTheme.dark(),
        home: Scaffold(body: child),
      );
    }

    testWidgets('a status chip shows an icon and text, not colour alone', (
      tester,
    ) async {
      await tester.pumpWidget(
        wrap(const NeoStatusChip(label: 'Online', tone: StatusTone.success)),
      );

      expect(find.text('Online'), findsOneWidget);
      expect(find.byType(Icon), findsOneWidget);
    });

    testWidgets('a banner shows an icon and the message', (tester) async {
      await tester.pumpWidget(
        wrap(const NeoBanner.error(message: 'Əlaqə yoxdur.')),
      );

      expect(find.text('Əlaqə yoxdur.'), findsOneWidget);
      expect(find.byIcon(Icons.error_outline), findsOneWidget);
    });

    testWidgets('a busy primary button is disabled and keeps its label', (
      tester,
    ) async {
      var pressed = 0;

      await tester.pumpWidget(
        wrap(
          NeoPrimaryButton(
            label: 'Daxil ol',
            busy: true,
            onPressed: () => pressed++,
          ),
        ),
      );

      await tester.tap(find.byType(FilledButton));
      await tester.pump();

      expect(pressed, 0);
      expect(find.byType(CircularProgressIndicator), findsOneWidget);

      // The label stays in the accessibility tree even while the spinner has
      // replaced it on screen, so a screen reader still knows what it is.
      expect(
        tester.getSemantics(find.byType(NeoPrimaryButton)).label,
        'Daxil ol',
      );
    });

    testWidgets('an idle primary button fires once', (tester) async {
      var pressed = 0;

      await tester.pumpWidget(
        wrap(NeoPrimaryButton(label: 'Təsdiqlə', onPressed: () => pressed++)),
      );

      await tester.tap(find.byType(FilledButton));
      await tester.pump();

      expect(pressed, 1);
      expect(find.text('Təsdiqlə'), findsOneWidget);
    });

    testWidgets('the widgets render in the dark theme too', (tester) async {
      await tester.pumpWidget(
        wrap(
          const NeoStatusChip(label: 'Offline', tone: StatusTone.danger),
          brightness: Brightness.dark,
        ),
      );

      expect(tester.takeException(), isNull);
      expect(find.text('Offline'), findsOneWidget);
    });
  });
}
