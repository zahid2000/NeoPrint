import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:image/image.dart' as img;

/// The look a scanned page gets (plan section 2.11).
enum ScanFilter { original, grayscale, blackWhite, contrast }

/// Part of the picture to keep, as fractions of its width and height after
/// the turn: 0,0 is the top left, 1,1 the bottom right.
final class ScanCrop {
  const ScanCrop(this.left, this.top, this.right, this.bottom);

  static const whole = ScanCrop(0, 0, 1, 1);

  final double left;
  final double top;
  final double right;
  final double bottom;

  bool get isWhole => left <= 0 && top <= 0 && right >= 1 && bottom >= 1;

  /// The crop with each side kept inside the picture and at least 5% across.
  ScanCrop clamped() {
    const minimum = 0.05;
    final l = left.clamp(0.0, 1.0 - minimum);
    final t = top.clamp(0.0, 1.0 - minimum);
    return ScanCrop(
      l,
      t,
      right.clamp(l + minimum, 1.0),
      bottom.clamp(t + minimum, 1.0),
    );
  }

  @override
  bool operator ==(Object other) =>
      other is ScanCrop &&
      other.left == left &&
      other.top == top &&
      other.right == right &&
      other.bottom == bottom;

  @override
  int get hashCode => Object.hash(left, top, right, bottom);
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
  }) => compute(_processMessage, _Message(source, edit, widthMm, heightMm));

  /// Applies [edit] and reduces the page to [maxDpi] for a document of
  /// [widthMm] × [heightMm] (both or neither). Never enlarges.
  static ProcessedScanPage process(
    Uint8List source,
    ScanPageEdit edit, {
    double? widthMm,
    double? heightMm,
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
    if (!crop.isWhole) {
      final x = (crop.left * page.width).round();
      final y = (crop.top * page.height).round();
      page = img.copyCrop(
        page,
        x: x,
        y: y,
        width: math.max(1, (crop.right * page.width).round() - x),
        height: math.max(1, (crop.bottom * page.height).round() - y),
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
  const _Message(this.source, this.edit, this.widthMm, this.heightMm);

  final Uint8List source;
  final ScanPageEdit edit;
  final double? widthMm;
  final double? heightMm;
}

ProcessedScanPage _processMessage(_Message message) => ScanProcessing.process(
  message.source,
  message.edit,
  widthMm: message.widthMm,
  heightMm: message.heightMm,
);
