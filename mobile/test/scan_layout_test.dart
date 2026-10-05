import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/features/layout/layout_model.dart';
import 'package:neoprint/features/layout/layout_templates.dart';
import 'package:neoprint/features/scanner/scan_models.dart';
import 'package:neoprint/features/scanner/scan_processing.dart';

/// Flow 9.7: front and back of an ID card on one A4, at real size.
void main() {
  const card = ScanPage(path: 'front', width: 1011, height: 638);
  const idCard = ScanType.preset(DocumentPreset.idCard);

  test('front and back of an ID card go on one A4 at 85.6 × 53.98 mm', () {
    final layout = scanLayout(
      [card, card],
      idCard,
      ScanArrangement.idFrontBack,
    );

    expect(layout.pages, hasLength(1));
    expect(layout.pages.single.elements, const [
      LayoutElement(
        image: 'p1.jpg',
        xMm: 62.2,
        yMm: 89.52,
        widthMm: 85.6,
        heightMm: 53.98,
      ),
      LayoutElement(
        image: 'p2.jpg',
        xMm: 62.2,
        yMm: 153.5,
        widthMm: 85.6,
        heightMm: 53.98,
      ),
    ]);
  });

  test('a card held upright prints upright, at the same real size', () {
    final upright = card.copyWith(edit: const ScanPageEdit(quarterTurns: 1));
    final element = scanLayout(
      [upright],
      idCard,
      ScanArrangement.onePerPage,
    ).pages.single.elements.single;

    expect((element.widthMm, element.heightMm), (53.98, 85.6));
  });

  test('a custom size is used as given', () {
    final element = scanLayout(
      [card],
      const ScanType.custom(100, 50),
      ScanArrangement.onePerPage,
    ).pages.single.elements.single;

    expect((element.widthMm, element.heightMm), (100.0, 50.0));
    expect(const ScanType.custom(0, 50).isValid, isFalse);
    expect(const ScanType.custom(100, 50).isValid, isTrue);
  });

  test('an identity card offers front and back first, only with two pages', () {
    expect(
      ScanArrangement.forType(idCard, 2).first,
      ScanArrangement.idFrontBack,
    );
    expect(
      ScanArrangement.forType(idCard, 1),
      isNot(contains(ScanArrangement.idFrontBack)),
    );
    expect(idCard.alwaysSensitive, isTrue);
  });
}
