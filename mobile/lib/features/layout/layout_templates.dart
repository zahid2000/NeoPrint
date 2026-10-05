import 'dart:math' as math;

import 'layout_model.dart';

/// A document type the scanner offers, with its real size (plan section 2.11).
///
/// The same list as `DocumentPresets` in `NeoPrint.Layout`; the backend knows
/// each by its [key] and checks the size and sensitivity again.
enum DocumentPreset {
  /// ISO/IEC 7810 ID-1: identity card, driving licence, bank card.
  idCard('IdCard', 85.6, 53.98, sensitive: true),
  id2('Id2', 105, 74, sensitive: true),

  /// ISO/IEC 7810 ID-3: a passport page.
  passport('Passport', 125, 88, sensitive: true),
  photo3x4('Photo3x4', 30, 40, sensitive: false),
  photo10x15('Photo10x15', 100, 150, sensitive: false),
  a6('A6', 105, 148, sensitive: false),
  a5('A5', 148, 210, sensitive: false),
  a4('A4', 210, 297, sensitive: false);

  const DocumentPreset(
    this.key,
    this.widthMm,
    this.heightMm, {
    required this.sensitive,
  });

  final String key;
  final double widthMm;
  final double heightMm;

  /// A personal document: always sensitive, deleted after printing by default.
  final bool sensitive;
}

/// The ready-made arrangements (plan section 2.11).
enum LayoutTemplate {
  /// Front and back of a card on one page, one above the other, centred.
  idFrontBack('IdFrontBack'),
  grid2x2('Grid2x2'),
  cards2x4('Cards2x4'),
  photo3x4x6('Photo3x4x6'),
  passportCentered('PassportCentered');

  const LayoutTemplate(this.wire);

  final String wire;

  static LayoutTemplate parse(String value) =>
      values.firstWhere((template) => template.wire == value);
}

/// A picture for a template, at the size it should print.
final class TemplatePicture {
  const TemplatePicture(this.image, this.widthMm, this.heightMm);

  factory TemplatePicture.fromJson(Map<String, dynamic> json) =>
      TemplatePicture(
        json['image'] as String,
        (json['widthMm'] as num).toDouble(),
        (json['heightMm'] as num).toDouble(),
      );

  final String image;
  final double widthMm;
  final double heightMm;
}

/// The ready-made arrangements and the default one-per-page layout.
///
/// The rules of `LayoutTemplates` in `NeoPrint.Layout`, line for line:
/// pictures go in at real size and are only ever reduced - uniformly - when
/// the block would not fit inside the margins. Checked against
/// `backend/tests/fixtures/layout/templates.json`, which the backend's tests
/// check too.
abstract final class LayoutTemplates {
  static const _cardGapMm = 10.0;
  static const _gridGapMm = 5.0;

  /// Arranges the pictures by a template on A4, portrait. Grids go round the
  /// pictures again: one photo fills "Foto 3×4 × 6" with six copies.
  static LayoutProject apply(
    LayoutTemplate template,
    List<TemplatePicture> pictures,
    double marginMm,
  ) {
    if (pictures.isEmpty) {
      throw ArgumentError('A template needs at least one picture.');
    }

    final page = switch (template) {
      LayoutTemplate.idFrontBack => _grid(
        1,
        math.min(2, pictures.length),
        pictures,
        _cardGapMm,
        marginMm,
        repeat: false,
      ),
      LayoutTemplate.grid2x2 => _grid(
        2,
        2,
        pictures,
        _gridGapMm,
        marginMm,
        repeat: true,
      ),
      LayoutTemplate.cards2x4 => _grid(
        2,
        4,
        pictures,
        _gridGapMm,
        marginMm,
        repeat: true,
      ),
      LayoutTemplate.photo3x4x6 => _grid(
        3,
        2,
        pictures,
        _gridGapMm,
        marginMm,
        repeat: true,
      ),
      LayoutTemplate.passportCentered => _grid(
        1,
        1,
        pictures,
        0,
        marginMm,
        repeat: false,
      ),
    };

    return LayoutProject(marginMm: marginMm, pages: [page]);
  }

  /// Each picture on its own A4 page, centred, at real size or reduced to
  /// fit the margins: what a scan gets before anything is arranged.
  static LayoutProject onePerPage(
    List<TemplatePicture> pictures,
    double marginMm,
  ) {
    if (pictures.isEmpty) {
      throw ArgumentError('A layout needs at least one picture.');
    }

    return LayoutProject(
      marginMm: marginMm,
      pages: [
        for (final picture in pictures)
          _grid(1, 1, [picture], 0, marginMm, repeat: false),
      ],
    );
  }

  static LayoutPage _grid(
    int columns,
    int rows,
    List<TemplatePicture> pictures,
    double gapMm,
    double marginMm, {
    required bool repeat,
  }) {
    const pageWidth = 210.0;
    const pageHeight = 297.0;

    final count = repeat
        ? columns * rows
        : math.min(columns * rows, pictures.length);
    final placed = [
      for (var index = 0; index < count; index++)
        pictures[index % pictures.length],
    ];

    final cellWidth = placed.map((p) => p.widthMm).reduce(math.max);
    final cellHeight = placed.map((p) => p.heightMm).reduce(math.max);
    final blockWidth = columns * cellWidth + (columns - 1) * gapMm;
    final blockHeight = rows * cellHeight + (rows - 1) * gapMm;

    final usableWidth = pageWidth - 2 * marginMm;
    final usableHeight = pageHeight - 2 * marginMm;
    final scale = math.min(
      1.0,
      math.min(usableWidth / blockWidth, usableHeight / blockHeight),
    );

    final left = (pageWidth - blockWidth * scale) / 2;
    final top = (pageHeight - blockHeight * scale) / 2;

    return LayoutPage([
      for (var index = 0; index < count; index++)
        _place(
          placed[index],
          left + (index % columns) * (cellWidth + gapMm) * scale,
          top + (index ~/ columns) * (cellHeight + gapMm) * scale,
          cellWidth * scale,
          cellHeight * scale,
          scale,
        ),
    ]);
  }

  static LayoutElement _place(
    TemplatePicture picture,
    double cellLeft,
    double cellTop,
    double cellWidth,
    double cellHeight,
    double scale,
  ) {
    final width = picture.widthMm * scale;
    final height = picture.heightMm * scale;

    return LayoutElement(
      image: picture.image,
      xMm: LayoutGeometry.round(cellLeft + (cellWidth - width) / 2),
      yMm: LayoutGeometry.round(cellTop + (cellHeight - height) / 2),
      widthMm: LayoutGeometry.round(width),
      heightMm: LayoutGeometry.round(height),
    );
  }
}
