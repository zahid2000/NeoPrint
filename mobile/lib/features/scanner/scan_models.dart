import '../layout/layout_model.dart';
import '../layout/layout_templates.dart';
import 'scan_processing.dart';

/// One page as the scanner gave it, with what the user has done to it.
final class ScanPage {
  const ScanPage({
    required this.path,
    required this.width,
    required this.height,
    this.edit = const ScanPageEdit(),
  });

  /// The scanner's JPEG in the app's cache.
  final String path;

  /// Its pixel size, as stored (before the edit).
  final int width;
  final int height;

  final ScanPageEdit edit;

  ScanPage copyWith({ScanPageEdit? edit}) => ScanPage(
    path: path,
    width: width,
    height: height,
    edit: edit ?? this.edit,
  );

  /// The shape after the turn and the crop: width over height.
  double get editedAspect {
    final turned = edit.quarterTurns.isOdd;
    final w = (turned ? height : width) * (edit.crop.right - edit.crop.left);
    final h = (turned ? width : height) * (edit.crop.bottom - edit.crop.top);
    return h <= 0 ? 1 : w / h;
  }
}

/// The document type: a preset with its real size, or a size the user gave.
final class ScanType {
  const ScanType.preset(DocumentPreset this.preset)
    : customWidthMm = null,
      customHeightMm = null;

  const ScanType.custom(this.customWidthMm, this.customHeightMm)
    : preset = null;

  final DocumentPreset? preset;
  final double? customWidthMm;
  final double? customHeightMm;

  bool get isCustom => preset == null;

  /// What the backend calls it: a preset key, or `Custom`.
  String get wire => preset?.key ?? 'Custom';

  double? get widthMm => preset?.widthMm ?? customWidthMm;
  double? get heightMm => preset?.heightMm ?? customHeightMm;

  /// A personal document is always sensitive; the user cannot turn it off.
  bool get alwaysSensitive => preset?.sensitive ?? false;

  /// Whether the custom size is usable: 1 to 1000 mm each way, as the backend checks.
  bool get isValid {
    final w = widthMm;
    final h = heightMm;
    return w != null && h != null && w >= 1 && w <= 1000 && h >= 1 && h <= 1000;
  }
}

/// How the pages are put on paper (plan section 2.11): each on its own page,
/// or one of the ready-made templates.
enum ScanArrangement {
  onePerPage(null),
  idFrontBack(LayoutTemplate.idFrontBack),
  grid2x2(LayoutTemplate.grid2x2),
  cards2x4(LayoutTemplate.cards2x4),
  photo3x4x6(LayoutTemplate.photo3x4x6),
  passportCentered(LayoutTemplate.passportCentered);

  const ScanArrangement(this.template);

  final LayoutTemplate? template;

  /// The arrangements that make sense for a document type, the usual first.
  static List<ScanArrangement> forType(ScanType type, int pages) =>
      switch (type.preset) {
        DocumentPreset.idCard || DocumentPreset.id2 => [
          if (pages >= 2) idFrontBack,
          onePerPage,
          cards2x4,
          grid2x2,
        ],
        DocumentPreset.passport => [passportCentered, onePerPage],
        DocumentPreset.photo3x4 => [photo3x4x6, onePerPage, grid2x2],
        DocumentPreset.photo10x15 => [onePerPage, grid2x2],
        _ => [onePerPage, grid2x2],
      };
}

/// The name a page travels under: the multipart part's file name and the
/// picture's name in the layout.
String scanPageName(int index) => 'p${index + 1}.jpg';

/// How the scan lies on paper (plan section 2.11): the pages at the
/// document's real size, held the way each page is turned, arranged by
/// [arrangement]. What `POST files/scan` draws the PDF from.
LayoutProject scanLayout(
  List<ScanPage> pages,
  ScanType type,
  ScanArrangement arrangement, {
  double marginMm = 10,
}) {
  final widthMm = type.widthMm ?? DocumentPreset.a4.widthMm;
  final heightMm = type.heightMm ?? DocumentPreset.a4.heightMm;
  final longMm = widthMm >= heightMm ? widthMm : heightMm;
  final shortMm = widthMm >= heightMm ? heightMm : widthMm;

  final pictures = [
    for (final (index, page) in pages.indexed)
      page.editedAspect >= 1
          ? TemplatePicture(scanPageName(index), longMm, shortMm)
          : TemplatePicture(scanPageName(index), shortMm, longMm),
  ];

  return switch (arrangement.template) {
    final template? => LayoutTemplates.apply(template, pictures, marginMm),
    null => LayoutTemplates.onePerPage(pictures, marginMm),
  };
}
