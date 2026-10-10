import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/l10n/app_localizations.dart';

/// The width of a small phone (a Samsung S24 is 360 dp wide).
const _phone = Size(360, 800);

void main() {
  setUpAll(() async {
    // The test font makes every glyph a square; widths are only real with
    // the app's own font.
    final bytes = await File('assets/fonts/Inter-Variable.ttf').readAsBytes();
    final loader = FontLoader('Inter')
      ..addFont(Future.value(ByteData.view(bytes.buffer)));
    await loader.load();
  });

  for (final language in ['az', 'en', 'ru']) {
    testWidgets('every tab label fits one line on a small phone ($language)', (
      tester,
    ) async {
      tester.view.physicalSize = _phone;
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);

      final l10n = lookupAppLocalizations(Locale(language));
      final labels = [
        l10n.navHome,
        l10n.navKiosks,
        l10n.navFiles,
        l10n.navScheduledTab,
        l10n.navProfile,
      ];

      await tester.pumpWidget(
        MaterialApp(
          theme: AppTheme.light(),
          home: Scaffold(
            bottomNavigationBar: NavigationBar(
              selectedIndex: 0,
              destinations: [
                for (final label in labels)
                  NavigationDestination(
                    icon: const Icon(Icons.circle_outlined),
                    label: label,
                  ),
              ],
            ),
          ),
        ),
      );

      for (final label in labels) {
        final paragraph = tester.renderObject<RenderParagraph>(
          find.descendant(
            of: find.text(label),
            matching: find.byType(RichText),
          ),
        );

        // Not cut short: the whole label is as wide as it wants to be.
        expect(
          paragraph.getMaxIntrinsicWidth(double.infinity),
          lessThanOrEqualTo(paragraph.size.width + 0.5),
          reason: '"$label" does not fit its tab',
        );
        expect(paragraph.didExceedMaxLines, isFalse, reason: label);
      }
    });
  }

  testWidgets('a label that is too long is cut, not wrapped', (tester) async {
    tester.view.physicalSize = _phone;
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    const long = 'Запланированные печати';

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: Scaffold(
          bottomNavigationBar: NavigationBar(
            selectedIndex: 0,
            destinations: [
              for (final label in ['A', 'B', 'C', long, 'D'])
                NavigationDestination(
                  icon: const Icon(Icons.circle_outlined),
                  label: label,
                ),
            ],
          ),
        ),
      ),
    );

    double height(String label) => tester
        .renderObject<RenderParagraph>(
          find.descendant(
            of: find.text(label),
            matching: find.byType(RichText),
          ),
        )
        .size
        .height;

    expect(height(long), height('A'));
  });
}
