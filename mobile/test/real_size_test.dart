import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/features/session/session_models.dart';
import 'package:neoprint/shared/widgets/option_widgets.dart';

/// Scans and layouts print at 100% on their paper, one page to a sheet
/// (plan section 2.11; docs/DESIGN_SYSTEM.md, "Real ölçü kilidi").
void main() {
  test('a scan starts at real size on its paper, anything else fitted', () {
    final scan = OrderOptions.forPaper('A4').toJson();
    final upload = OrderOptions.forPaper(null).toJson();

    expect(scan['scaling'], 'Actual');
    expect(scan['paper'], 'A4');
    expect(scan['pagesPerSheet'], 1);
    expect(upload['scaling'], 'Fit');

    // Changing the copies keeps it at real size.
    expect(OrderOptions.forPaper('A3').copyWith(copies: 3).toJson(), {
      ...OrderOptions.forPaper('A3').toJson(),
      'copies': 3,
    });
  });

  testWidgets('a locked choice shows its value and cannot be changed', (
    tester,
  ) async {
    String? changed;

    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: OptionChoice<String>(
            atKiosk: false,
            atKioskLabel: '',
            value: 'A4',
            choices: const [('A4', 'A4', true), ('A3', 'A3', true)],
            onChanged: (value) => changed = value,
            locked: true,
          ),
        ),
      ),
    );

    final button = tester.widget<SegmentedButton<String?>>(
      find.byType(SegmentedButton<String?>),
    );
    expect(button.selected, {'A4'});
    expect(button.onSelectionChanged, isNull);

    await tester.tap(find.text('A3'));
    expect(changed, isNull);
  });
}
