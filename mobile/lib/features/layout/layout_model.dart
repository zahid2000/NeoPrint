import 'dart:math' as math;

/// The paper a layout is drawn for (plan section 2.11: A4 / A3, plus A5).
enum LayoutPaper {
  a4('A4', 210, 297),
  a3('A3', 297, 420),
  a5('A5', 148, 210);

  const LayoutPaper(this.wire, this.shortMm, this.longMm);

  final String wire;
  final double shortMm;
  final double longMm;

  static LayoutPaper parse(String? value) =>
      values.firstWhere((paper) => paper.wire == value, orElse: () => a4);
}

enum LayoutOrientation {
  portrait('Portrait'),
  landscape('Landscape');

  const LayoutOrientation(this.wire);

  final String wire;

  static LayoutOrientation parse(String? value) => values.firstWhere(
    (orientation) => orientation.wire == value,
    orElse: () => portrait,
  );
}

/// One picture on a page, in millimetres from the paper's top-left corner.
///
/// The same JSON as `NeoPrint.Layout` on the backend and the kiosk, so a
/// layout made here prints exactly as drawn there. The box is the printed
/// size of the picture whatever its angle; [rotationDeg] turns it clockwise
/// about the box's centre.
final class LayoutElement {
  const LayoutElement({
    required this.image,
    required this.xMm,
    required this.yMm,
    required this.widthMm,
    required this.heightMm,
    this.rotationDeg = 0,
  });

  factory LayoutElement.fromJson(Map<String, dynamic> json) => LayoutElement(
    image: json['image'] as String,
    xMm: (json['xMm'] as num).toDouble(),
    yMm: (json['yMm'] as num).toDouble(),
    widthMm: (json['widthMm'] as num).toDouble(),
    heightMm: (json['heightMm'] as num).toDouble(),
    rotationDeg: (json['rotationDeg'] as num? ?? 0).toDouble(),
  );

  /// Which picture: a scan page's name, `file:{id}`, `file:{id}:{page}` or `asset:{key}`.
  final String image;
  final double xMm;
  final double yMm;
  final double widthMm;
  final double heightMm;
  final double rotationDeg;

  LayoutElement copyWith({
    String? image,
    double? xMm,
    double? yMm,
    double? widthMm,
    double? heightMm,
    double? rotationDeg,
  }) => LayoutElement(
    image: image ?? this.image,
    xMm: xMm ?? this.xMm,
    yMm: yMm ?? this.yMm,
    widthMm: widthMm ?? this.widthMm,
    heightMm: heightMm ?? this.heightMm,
    rotationDeg: rotationDeg ?? this.rotationDeg,
  );

  Map<String, dynamic> toJson() => {
    'image': image,
    'xMm': xMm,
    'yMm': yMm,
    'widthMm': widthMm,
    'heightMm': heightMm,
    'rotationDeg': rotationDeg,
  };

  @override
  bool operator ==(Object other) =>
      other is LayoutElement &&
      other.image == image &&
      other.xMm == xMm &&
      other.yMm == yMm &&
      other.widthMm == widthMm &&
      other.heightMm == heightMm &&
      other.rotationDeg == rotationDeg;

  @override
  int get hashCode =>
      Object.hash(image, xMm, yMm, widthMm, heightMm, rotationDeg);

  @override
  String toString() =>
      'LayoutElement($image, $xMm, $yMm, $widthMm × $heightMm, $rotationDeg°)';
}

/// One sheet side. Later elements are drawn over earlier ones.
final class LayoutPage {
  const LayoutPage(this.elements);

  factory LayoutPage.fromJson(Map<String, dynamic> json) => LayoutPage([
    for (final element in json['elements'] as List<dynamic>)
      LayoutElement.fromJson(element as Map<String, dynamic>),
  ]);

  final List<LayoutElement> elements;

  Map<String, dynamic> toJson() => {
    'elements': [for (final element in elements) element.toJson()],
  };
}

/// A page layout (plan section 2.11, `LayoutProjectJson`).
final class LayoutProject {
  const LayoutProject({
    this.version = currentVersion,
    this.paper = LayoutPaper.a4,
    this.orientation = LayoutOrientation.portrait,
    required this.marginMm,
    required this.pages,
  });

  factory LayoutProject.fromJson(Map<String, dynamic> json) => LayoutProject(
    version: (json['version'] as num).toInt(),
    paper: LayoutPaper.parse(json['paper'] as String?),
    orientation: LayoutOrientation.parse(json['orientation'] as String?),
    marginMm: (json['marginMm'] as num).toDouble(),
    pages: [
      for (final page in json['pages'] as List<dynamic>)
        LayoutPage.fromJson(page as Map<String, dynamic>),
    ],
  );

  static const currentVersion = 1;

  final int version;
  final LayoutPaper paper;
  final LayoutOrientation orientation;
  final double marginMm;
  final List<LayoutPage> pages;

  /// Width and height of the paper as it lies.
  (double, double) get pageSizeMm => orientation == LayoutOrientation.landscape
      ? (paper.longMm, paper.shortMm)
      : (paper.shortMm, paper.longMm);

  /// Every picture the layout uses, once each, in order of first use.
  List<String> get images => {
    for (final page in pages)
      for (final element in page.elements) element.image,
  }.toList();

  LayoutProject copyWith({
    LayoutPaper? paper,
    LayoutOrientation? orientation,
    double? marginMm,
    List<LayoutPage>? pages,
  }) => LayoutProject(
    version: version,
    paper: paper ?? this.paper,
    orientation: orientation ?? this.orientation,
    marginMm: marginMm ?? this.marginMm,
    pages: pages ?? this.pages,
  );

  Map<String, dynamic> toJson() => {
    'version': version,
    'paper': paper.wire,
    'orientation': orientation.wire,
    'marginMm': marginMm,
    'pages': [for (final page in pages) page.toJson()],
  };
}

/// An upright rectangle in millimetres.
final class RectMm {
  const RectMm(this.left, this.top, this.right, this.bottom);

  final double left;
  final double top;
  final double right;
  final double bottom;

  double get width => right - left;
  double get height => bottom - top;
}

/// Measurements the editors share with the kiosk (`LayoutGeometry`).
abstract final class LayoutGeometry {
  /// The edge a printer cannot print into when it does not say (plan section 2.11).
  static const defaultPrinterMarginMm = 4.0;

  /// The smallest upright rectangle holding the picture as turned.
  static RectMm bounds(LayoutElement element) {
    final radians = element.rotationDeg * math.pi / 180;
    final cos = math.cos(radians).abs();
    final sin = math.sin(radians).abs();
    final width = element.widthMm * cos + element.heightMm * sin;
    final height = element.widthMm * sin + element.heightMm * cos;
    final centreX = element.xMm + element.widthMm / 2;
    final centreY = element.yMm + element.heightMm / 2;

    return RectMm(
      centreX - width / 2,
      centreY - height / 2,
      centreX + width / 2,
      centreY + height / 2,
    );
  }

  /// Whether part of the picture falls where the printer cannot print.
  static bool leavesPrintableArea(
    LayoutProject project,
    LayoutElement element, {
    double printerMarginMm = defaultPrinterMarginMm,
  }) {
    const tolerance = 0.01;
    final (width, height) = project.pageSizeMm;
    final box = bounds(element);

    return box.left < printerMarginMm - tolerance ||
        box.top < printerMarginMm - tolerance ||
        box.right > width - printerMarginMm + tolerance ||
        box.bottom > height - printerMarginMm + tolerance;
  }

  /// Rounds to a hundredth of a millimetre, half away from zero, as the
  /// backend does (`Math.Round(x, 2, AwayFromZero)`).
  static double round(double millimetres) => (millimetres * 100).round() / 100;
}
