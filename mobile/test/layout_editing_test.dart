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

  group('arrange (T-402)', () {
    const a4 = LayoutProject(marginMm: 10, pages: [LayoutPage([])]);

    test('several pictures sit side by side inside the margins, none on '
        'another', () {
      final pictures = [
        for (var i = 0; i < 6; i++)
          EditorPicture(name: 'p$i.jpg', aspect: 210 / 297),
      ];

      final placed = LayoutEditing.arrange(a4, 0, pictures).pages[0].elements;

      expect(placed, hasLength(6));
      expect(placed.map((e) => e.image), [
        for (var i = 0; i < 6; i++) 'p$i.jpg',
      ]);
      for (final e in placed) {
        expect(e.xMm, greaterThanOrEqualTo(10 - 0.1));
        expect(e.yMm, greaterThanOrEqualTo(10 - 0.1));
        expect(e.xMm + e.widthMm, lessThanOrEqualTo(200 + 0.1));
        expect(e.yMm + e.heightMm, lessThanOrEqualTo(287 + 0.1));
      }
      for (var i = 0; i < placed.length; i++) {
        for (var j = i + 1; j < placed.length; j++) {
          final a = placed[i];
          final b = placed[j];
          final apart =
              a.xMm + a.widthMm <= b.xMm + 0.1 ||
              b.xMm + b.widthMm <= a.xMm + 0.1 ||
              a.yMm + a.heightMm <= b.yMm + 0.1 ||
              b.yMm + b.heightMm <= a.yMm + 0.1;
          expect(apart, isTrue, reason: '$i and $j overlap');
        }
      }
    });

    test('two wide pictures go one under the other', () {
      final placed = LayoutEditing.arrange(a4, 0, const [
        EditorPicture(name: 'a.jpg', aspect: 2),
        EditorPicture(name: 'b.jpg', aspect: 2),
      ]).pages[0].elements;

      expect(placed[0].xMm, placed[1].xMm);
      expect(placed[1].yMm, greaterThan(placed[0].yMm + placed[0].heightMm));
    });
  });

  group('several pictures at once (T-430)', () {
    const empty = LayoutProject(marginMm: 10, pages: [LayoutPage([])]);
    final pictures = {
      for (var i = 0; i < 12; i++)
        'p$i': EditorPicture(name: 'p$i', aspect: i.isEven ? 1.5 : 0.75),
    };

    test('more than a page takes go on new pages after it', () {
      final laid = LayoutEditing.arrangePaged(
        empty,
        0,
        pictures.values.toList(),
      );

      expect(laid.pages, hasLength(2));
      expect(laid.pages[0].elements, hasLength(LayoutEditing.perPage));
      expect(laid.pages[1].elements, hasLength(12 - LayoutEditing.perPage));
      expect(LayoutEditing.hasOffPage(laid), isFalse);
    });

    test('a style given keeps each picture its shape and its centre', () {
      final laid = LayoutEditing.arrange(empty, 0, [
        pictures['p0']!,
        pictures['p1']!,
      ]);
      final source = laid.pages[0].elements[0].copyWith(
        widthMm: 60,
        rotationDeg: 90,
      );
      final before = laid.pages[0].elements[1];

      final styled = LayoutEditing.applyStyle(laid, 0, [1], source, pictures);
      final after = styled.pages[0].elements[1];

      expect(after.widthMm, 60);
      expect(after.heightMm, closeTo(80, 0.1), reason: 'p1 is 3:4');
      expect(after.rotationDeg, 90);
      expect(
        after.xMm + after.widthMm / 2,
        closeTo(before.xMm + before.widthMm / 2, 0.1),
      );
    });

    test(
      'chosen ones laid out evenly, to a new page, turned or removed together',
      () {
        final laid = LayoutEditing.arrange(
          empty,
          0,
          pictures.values.take(4).toList(),
        );

        final moved = LayoutEditing.moveToNewPage(laid, 0, {1, 3}, pictures);
        expect(moved.pages, hasLength(2));
        expect(moved.pages[0].elements.map((e) => e.image), ['p0', 'p2']);
        expect(moved.pages[1].elements.map((e) => e.image), ['p1', 'p3']);

        final turned = LayoutEditing.rotateMany(laid, 0, {0, 2});
        expect(turned.pages[0].elements.map((e) => e.rotationDeg), [
          90,
          0,
          90,
          0,
        ]);

        expect(
          LayoutEditing.deleteMany(laid, 0, {0, 1}).pages[0].elements,
          hasLength(2),
        );

        final even = LayoutEditing.arrangeElements(laid, 0, {
          0,
          1,
          2,
          3,
        }, pictures);
        expect(even.pages[0].elements, hasLength(4));
        expect(LayoutEditing.hasOffPage(even), isFalse);
      },
    );
  });
}
