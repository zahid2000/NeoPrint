import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/features/layout/layout_model.dart';
import 'package:neoprint/features/layout/layout_templates.dart';

/// The arrangements a phone makes must be the backend's and the kiosk's to
/// the hundredth of a millimetre (plan section 2.11): both check themselves
/// against the same fixture.
void main() {
  final fixture = jsonDecode(
    File('../backend/tests/fixtures/layout/templates.json').readAsStringSync(),
  ) as List<dynamic>;

  for (final raw in fixture) {
    final item = raw as Map<String, dynamic>;

    test('template ${item['name']} matches the backend', () {
      final pictures = [
        for (final picture in item['pictures'] as List<dynamic>)
          TemplatePicture.fromJson(picture as Map<String, dynamic>),
      ];
      final margin = (item['marginMm'] as num).toDouble();
      final built = item['template'] == 'OnePerPage'
          ? LayoutTemplates.onePerPage(pictures, margin)
          : LayoutTemplates.apply(
              LayoutTemplate.parse(item['template'] as String),
              pictures,
              margin,
            );

      expect(built.toJson(), item['expected']);
    });
  }

  test('the layout JSON reads back unchanged', () {
    final project = LayoutTemplates.apply(LayoutTemplate.idFrontBack, const [
      TemplatePicture('front', 85.6, 53.98),
      TemplatePicture('back', 85.6, 53.98),
    ], 10);

    final again = LayoutProject.fromJson(
      jsonDecode(jsonEncode(project.toJson())) as Map<String, dynamic>,
    );

    expect(again.toJson(), project.toJson());
    expect(again.images, ['front', 'back']);
  });

  test('a card near the edge leaves the printable area', () {
    const project = LayoutProject(marginMm: 10, pages: []);
    const inside = LayoutElement(
      image: 'a',
      xMm: 4,
      yMm: 4,
      widthMm: 85.6,
      heightMm: 53.98,
    );

    expect(LayoutGeometry.leavesPrintableArea(project, inside), isFalse);
    expect(
      LayoutGeometry.leavesPrintableArea(project, inside.copyWith(xMm: 3)),
      isTrue,
    );
    expect(
      LayoutGeometry.bounds(inside.copyWith(xMm: 10, yMm: 40, rotationDeg: 90))
          .left,
      closeTo(25.81, 0.001),
    );
  });

  test('an identity card is personal, a photo is not', () {
    expect(DocumentPreset.idCard.sensitive, isTrue);
    expect(DocumentPreset.idCard.widthMm, 85.6);
    expect(DocumentPreset.photo3x4.sensitive, isFalse);
  });
}
