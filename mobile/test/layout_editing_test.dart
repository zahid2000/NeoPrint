import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/features/layout/layout_editing.dart';
import 'package:neoprint/features/layout/layout_model.dart';

/// The layout editor's coordinates (plan section 11: "layout editor
/// koordinatları"), in millimetres on A4.
void main() {
  const card = LayoutElement(
    image: 'card',
    xMm: 50,
    yMm: 50,
    widthMm: 85.6,
    heightMm: 53.98,
  );
  const project = LayoutProject(
    marginMm: 10,
    pages: [
      LayoutPage([card]),
    ],
  );

  test('a picture dragged near the margin snaps onto it and says where', () {
    final (moved, lines) = LayoutEditing.move(project, 0, 0, card, -38.5, 0);

    expect(moved.xMm, 10);
    expect(lines, contains(const SnapLine.vertical(10)));
  });

  test('a picture dragged near the middle centres on the page', () {
    // Card centre at 92.8 + 13.5 = 106.3; the page centre is 105.
    final (moved, lines) = LayoutEditing.move(project, 0, 0, card, 13.5, 0);

    expect(moved.xMm + moved.widthMm / 2, 105);
    expect(lines, contains(const SnapLine.vertical(105)));
  });

  test('far from anything a move is exact', () {
    final (moved, lines) = LayoutEditing.move(project, 0, 0, card, 7.3, 11.1);

    expect((moved.xMm, moved.yMm), (57.3, 61.1));
    expect(lines, isEmpty);
  });

  test('a second picture snaps to the first one\'s edge', () {
    const other = LayoutElement(
      image: 'b',
      xMm: 50,
      yMm: 120,
      widthMm: 40,
      heightMm: 40,
    );
    const two = LayoutProject(
      marginMm: 10,
      pages: [
        LayoutPage([card, other]),
      ],
    );

    // Its left edge 51.2 is 1.2 mm from the card's left edge at 50.
    final (moved, _) = LayoutEditing.move(two, 0, 1, other, 1.2, 0);

    expect(moved.xMm, 50);
  });

  test('scaling with the ratio locked keeps the shape about the centre', () {
    final bigger = LayoutEditing.scale(card, 2, 1.1);

    expect(bigger.widthMm, 171.2);
    expect(bigger.heightMm, 107.96);
    expect(bigger.xMm + bigger.widthMm / 2, closeTo(92.8, 0.01));
  });

  test('scaling never goes under 1 mm', () {
    final tiny = LayoutEditing.scale(card, 0.001, 0.001);

    expect(tiny.heightMm, greaterThanOrEqualTo(1));
    expect(tiny.widthMm / tiny.heightMm, closeTo(85.6 / 53.98, 0.05));
  });

  test('a turn close to a right angle settles on it', () {
    expect(LayoutEditing.rotate(card, 88).rotationDeg, 90);
    expect(LayoutEditing.rotate(card, 80).rotationDeg, 80);
    expect(LayoutEditing.rotate(card, -2).rotationDeg, 0);
    expect(
      LayoutEditing.rotate90(card.copyWith(rotationDeg: 45)).rotationDeg,
      90,
    );
    expect(
      LayoutEditing.rotate90(card.copyWith(rotationDeg: 270)).rotationDeg,
      0,
    );
  });

  test('back to real size keeps the centre and the way it is held', () {
    const picture = EditorPicture(
      name: 'card',
      aspect: 1.586,
      realWidthMm: 85.6,
      realHeightMm: 53.98,
    );
    final enlarged = card.copyWith(
      widthMm: 171.2,
      heightMm: 107.96,
      xMm: 7.2,
      yMm: 23.01,
    );
    final upright = card.copyWith(widthMm: 40, heightMm: 60);

    final real = LayoutEditing.realSize(enlarged, picture);
    expect((real.widthMm, real.heightMm), (85.6, 53.98));
    expect(real.xMm + real.widthMm / 2, closeTo(92.8, 0.01));
    expect(
      (
        LayoutEditing.realSize(upright, picture).widthMm,
        LayoutEditing.realSize(upright, picture).heightMm,
      ),
      (53.98, 85.6),
    );
  });

  test('duplicate, order and delete', () {
    var edited = LayoutEditing.duplicate(project, 0, 0);
    expect(edited.pages[0].elements, hasLength(2));
    expect(edited.pages[0].elements[1].xMm, 55);

    edited = LayoutEditing.toBack(edited, 0, 1);
    expect(edited.pages[0].elements[0].xMm, 55);

    edited = LayoutEditing.toFront(edited, 0, 0);
    expect(edited.pages[0].elements[1].xMm, 55);

    edited = LayoutEditing.delete(edited, 0, 0);
    expect(edited.pages[0].elements.single.xMm, 55);
  });

  test(
    'a new picture goes in the middle at real size, or as large as fits',
    () {
      const scan = EditorPicture(
        name: 's',
        aspect: 1.586,
        realWidthMm: 85.6,
        realHeightMm: 53.98,
      );
      const photo = EditorPicture(name: 'p', aspect: 0.75);

      final withScan = LayoutEditing.add(
        project,
        0,
        scan,
      ).pages[0].elements.last;
      final withPhoto = LayoutEditing.add(
        project,
        0,
        photo,
      ).pages[0].elements.last;

      expect((withScan.xMm, withScan.yMm), (62.2, 121.51));
      // A 3:4 photo across the 190 mm would be 253 mm tall: it fits.
      expect(withPhoto.widthMm, 190);
      expect(withPhoto.heightMm, closeTo(253.33, 0.01));
    },
  );

  test('a picture off the paper blocks saving; near the edge only warns', () {
    final near = LayoutEditing.replace(project, 0, 0, card.copyWith(xMm: 2));
    final off = LayoutEditing.replace(project, 0, 0, card.copyWith(xMm: 200));

    expect(LayoutEditing.leavesPrintableArea(near), isTrue);
    expect(LayoutEditing.hasOffPage(near), isFalse);
    expect(LayoutEditing.hasOffPage(off), isTrue);
  });

  test('centring puts the picture in the middle of the paper', () {
    final centred = LayoutEditing.centre(project, card);

    expect((centred.xMm, centred.yMm), (62.2, 121.51));
  });
}
