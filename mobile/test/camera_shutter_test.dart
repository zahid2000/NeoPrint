import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:neoprint/core/l10n/app_delegates.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/files/camera_capture_screen.dart';
import 'package:neoprint/l10n/app_localizations.dart';

void main() {
  for (final multiple in [false, true]) {
    testWidgets('the shutter is laid out with the app theme, '
        'multiple: $multiple (T-470)', (tester) async {
      var taken = 0;
      await tester.pumpWidget(
        MaterialApp(
          theme: AppTheme.light(),
          locale: const Locale('az'),
          localizationsDelegates: appLocalizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(
            body: Column(
              children: [
                const Expanded(child: SizedBox()),
                CameraShutterRow(
                  multiple: multiple,
                  capturing: false,
                  full: false,
                  onCapture: () => taken++,
                  onDone: null,
                ),
              ],
            ),
          ),
        ),
      );

      expect(tester.takeException(), isNull);
      final shutter = find.byKey(const ValueKey('cameraCapture'));
      expect(tester.getSize(shutter).width, greaterThan(100));
      await tester.tap(shutter);
      expect(taken, 1);

      final done = find.byKey(const ValueKey('cameraDone'));
      expect(done, multiple ? findsOneWidget : findsNothing);
      if (multiple) {
        expect(tester.getSize(done).width, greaterThan(100));
      }
    });
  }
}
