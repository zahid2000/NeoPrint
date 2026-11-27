import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:image/image.dart' as img;

/// The look a scanned page gets (plan section 2.11).
enum ScanFilter { original, grayscale, blackWhite, contrast }

/// A point of the crop, as fractions of the picture's width and height
/// after the turn: 0,0 is the top left, 1,1 the bottom right.
typedef ScanPoint = ({double x, double y});

/// Part of the picture to keep: any four-sided shape (T-476), its corners
/// moved one by one as in a document scanner app. A rectangle is cut out
/// as it is; any other shape is straightened onto a rectangle - the page
/// photographed at a slant comes out flat.
final class ScanCrop {
  const ScanCrop(
    this.topLeft,
    this.topRight,
    this.bottomRight,
    this.bottomLeft,
  );

  /// A rectangle: [left] to [right], [top] to [bottom].
  const ScanCrop.rect(double left, double top, double right, double bottom)
    : topLeft = (x: left, y: top),
      topRight = (x: right, y: top),
      bottomRight = (x: right, y: bottom),
      bottomLeft = (x: left, y: bottom);

  static const whole = ScanCrop.rect(0, 0, 1, 1);

  final ScanPoint topLeft;
  final ScanPoint topRight;
  final ScanPoint bottomRight;
  final ScanPoint bottomLeft;

  /// Clockwise from the top left.
  List<ScanPoint> get corners => [topLeft, topRight, bottomRight, bottomLeft];

  /// The crop with [index] (clockwise from the top left) at [point].
  ScanCrop withCorner(int index, ScanPoint point) {
    final next = [...corners]..[index] = point;
    return ScanCrop(next[0], next[1], next[2], next[3]);
  }

  bool get isWhole => this == whole;

  /// Whether the sides run along the picture's: then it is a plain cut.
  bool get isRect =>
      topLeft.y == topRight.y &&
      bottomLeft.y == bottomRight.y &&
      topLeft.x == bottomLeft.x &&
      topRight.x == bottomRight.x;

  /// The corners kept inside the picture.
  ScanCrop clamped() {
    ScanPoint inside(ScanPoint p) =>
        (x: p.x.clamp(0.0, 1.0), y: p.y.clamp(0.0, 1.0));
    return ScanCrop(
      inside(topLeft),
      inside(topRight),
      inside(bottomRight),
      inside(bottomLeft),
    );
  }

  /// Whether the shape can be straightened: its corners go round one way
  /// with no side crossing another, and it is not a sliver - every side at
  /// least 5% of the picture across.
  bool get isUsable {
    final c = corners;
    double? sign;
    for (var i = 0; i < 4; i++) {
      final a = c[i], b = c[(i + 1) % 4], d = c[(i + 2) % 4];
      final cross = (b.x - a.x) * (d.y - b.y) - (b.y - a.y) * (d.x - b.x);
      if (cross.abs() < 1e-9) {
        return false;
      }
      if (sign != null && cross.sign != sign) {
        return false;
      }
      sign = cross.sign;
    }
    for (var i = 0; i < 4; i++) {
      final a = c[i], b = c[(i + 1) % 4];
      if (math.sqrt(math.pow(b.x - a.x, 2) + math.pow(b.y - a.y, 2)) < 0.05) {
        return false;
      }
    }
    return true;
  }

  /// The flat page's size in pixels for a picture of [width] x [height]:
  /// the longer of each pair of opposite sides.
  (int, int) sizeIn(int width, int height) {
    double side(ScanPoint a, ScanPoint b) => math.sqrt(
      math.pow((b.x - a.x) * width, 2) + math.pow((b.y - a.y) * height, 2),
    );
    final w = math.max(side(topLeft, topRight), side(bottomLeft, bottomRight));
    final h = math.max(side(topLeft, bottomLeft), side(topRight, bottomRight));
    return (math.max(1, w.round()), math.max(1, h.round()));
  }

  @override
  bool operator ==(Object other) =>
      other is ScanCrop &&
      other.topLeft == topLeft &&
      other.topRight == topRight &&
      other.bottomRight == bottomRight &&
      other.bottomLeft == bottomLeft;

  @override
  int get hashCode => Object.hash(topLeft, topRight, bottomRight, bottomLeft);
}

/// What is done to one page, applied in this order: turn, crop, filter,
/// brightness, then the size.
final class ScanPageEdit {
  const ScanPageEdit({
    this.quarterTurns = 0,
    this.crop = ScanCrop.whole,
    this.filter = ScanFilter.original,
    this.brightness = 0,
  });

  /// Clockwise quarter turns, 0–3.
  final int quarterTurns;
  final ScanCrop crop;
  final ScanFilter filter;

  /// −100 to 100; 0 leaves it.
  final int brightness;

  ScanPageEdit copyWith({
    int? quarterTurns,
    ScanCrop? crop,
    ScanFilter? filter,
    int? brightness,
  }) => ScanPageEdit(
    quarterTurns: quarterTurns ?? this.quarterTurns,
    crop: crop ?? this.crop,
    filter: filter ?? this.filter,
    brightness: brightness ?? this.brightness,
  );
}

/// The page ready to send: JPEG bytes and their pixel size.
final class ProcessedScanPage {
  const ProcessedScanPage(this.jpeg, this.width, this.height);

  final Uint8List jpeg;
  final int width;
  final int height;
}

/// Turns a scanned page into the JPEG that is sent (plan section 2.11): at
/// most 300 dpi for the document's real size, so an ID card is about
/// 1011 × 638 pixels - enough for any kiosk printer, small enough to upload.
abstract final class ScanProcessing {
  /// The highest resolution kept, as on the backend (`LayoutRenderer.MaxDpi`).
  static const maxDpi = 300;

  static const _jpegQuality = 90;

  /// Runs [process] on a background isolate.
  static Future<ProcessedScanPage> processInBackground(
    Uint8List source,
    ScanPageEdit edit, {
    double? widthMm,
    double? heightMm,
    int? maxSide,
  }) => compute(
    _processMessage,
    _Message(source, edit, widthMm, heightMm, maxSide),
  );

  /// Applies [edit] and reduces the page to [maxDpi] for a document of
  /// [widthMm] × [heightMm] (both or neither), or to [maxSide] pixels on its
  /// long side for a preview. Never enlarges.
  static ProcessedScanPage process(
    Uint8List source,
    ScanPageEdit edit, {
    double? widthMm,
    double? heightMm,
    int? maxSide,
  }) {
    img.Image? decoded;

    try {
      decoded = img.decodeImage(source);
    } on Object {
      // The decoders throw range errors on bytes that are not an image.
      decoded = null;
    }

    if (decoded == null) {
      throw const FormatException('The scanner returned an unreadable page.');
    }

    // The camera may record a turn instead of turning the pixels.
    var page = img.bakeOrientation(decoded);

    final turns = edit.quarterTurns % 4;
    if (turns != 0) {
      page = img.copyRotate(page, angle: 90 * turns);
    }

    final crop = edit.crop.clamped();
    if (crop.isWhole) {
      // Nothing to cut.
    } else if (crop.isRect) {
      final x = (crop.topLeft.x * page.width).round();
      final y = (crop.topLeft.y * page.height).round();
      page = img.copyCrop(
        page,
        x: x,
        y: y,
        width: math.max(1, (crop.bottomRight.x * page.width).round() - x),
        height: math.max(1, (crop.bottomRight.y * page.height).round() - y),
      );
    } else {
      // Straightened onto a flat page of its own size (T-476).
      final (w, h) = crop.sizeIn(page.width, page.height);
      img.Point at(ScanPoint p) =>
          img.Point(p.x * (page.width - 1), p.y * (page.height - 1));
      page = img.copyRectify(
        page,
        topLeft: at(crop.topLeft),
        topRight: at(crop.topRight),
        bottomLeft: at(crop.bottomLeft),
        bottomRight: at(crop.bottomRight),
        interpolation: img.Interpolation.linear,
        toImage: img.Image(width: w, height: h, numChannels: page.numChannels),
      );
    }

    page = switch (edit.filter) {
      ScanFilter.original => page,
      ScanFilter.grayscale => img.grayscale(page),
      // Text and stamps on white: every pixel black or white.
      ScanFilter.blackWhite => img.luminanceThreshold(page, threshold: 0.55),
      ScanFilter.contrast => img.adjustColor(page, contrast: 1.5),
    };

    final brightness = edit.brightness.clamp(-100, 100);
    if (brightness != 0) {
      page = img.adjustColor(page, brightness: 1 + brightness / 100);
    }

    if (widthMm != null && heightMm != null) {
      page = _reduce(page, widthMm, heightMm);
    }

    if (maxSide != null && math.max(page.width, page.height) > maxSide) {
      page = img.copyResize(
        page,
        width: page.width >= page.height ? maxSide : null,
        height: page.width >= page.height ? null : maxSide,
        interpolation: img.Interpolation.average,
      );
    }

    return ProcessedScanPage(
      img.encodeJpg(page, quality: _jpegQuality),
      page.width,
      page.height,
    );
  }

  /// Pixels across the longer side of a [mm] side at [maxDpi].
  static int pixelsFor(double mm) => (mm / 25.4 * maxDpi).ceil();

  static img.Image _reduce(img.Image page, double widthMm, double heightMm) {
    // The page may be held either way round: match the document's long side
    // to the page's long side.
    final longMm = math.max(widthMm, heightMm);
    final shortMm = math.min(widthMm, heightMm);
    final landscape = page.width >= page.height;
    final maxWidth = pixelsFor(landscape ? longMm : shortMm);
    final maxHeight = pixelsFor(landscape ? shortMm : longMm);
    final scale = math.min(
      1.0,
      math.min(maxWidth / page.width, maxHeight / page.height),
    );

    if (scale >= 1.0) {
      return page;
    }

    return img.copyResize(
      page,
      width: math.max(1, (page.width * scale).round()),
      height: math.max(1, (page.height * scale).round()),
      interpolation: img.Interpolation.average,
    );
  }
}

final class _Message {
  const _Message(
    this.source,
    this.edit,
    this.widthMm,
    this.heightMm,
    this.maxSide,
  );

  final Uint8List source;
  final ScanPageEdit edit;
  final double? widthMm;
  final double? heightMm;
  final int? maxSide;
}

ProcessedScanPage _processMessage(_Message message) => ScanProcessing.process(
  message.source,
  message.edit,
  widthMm: message.widthMm,
  heightMm: message.heightMm,
  maxSide: message.maxSide,
);
