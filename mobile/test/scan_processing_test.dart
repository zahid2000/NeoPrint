import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:image/image.dart' as img;
import 'package:neoprint/features/scanner/scan_processing.dart';

/// A scanned page after the scanner (plan section 2.11): turned, cut,
/// filtered and brought to 300 dpi for its real size.
void main() {
  Uint8List photo(int width, int height) {
    final image = img.Image(width: width, height: height);
    for (var y = 0; y < height; y++) {
      for (var x = 0; x < width; x++) {
        image.setPixelRgb(x, y, x * 255 ~/ width, y * 255 ~/ height, 120);
      }
    }
    return img.encodeJpg(image, quality: 95);
  }

  test('an ID card photo is reduced to 300 dpi for 85.6 × 53.98 mm', () {
    final page = ScanProcessing.process(
      photo(3000, 1891),
      const ScanPageEdit(),
      widthMm: 85.6,
      heightMm: 53.98,
    );

    // 85.6 mm at 300 dpi is 1011.0 pixels; 53.98 mm is 637.6.
    expect(page.width, lessThanOrEqualTo(1012));
    expect(page.height, lessThanOrEqualTo(638));
    expect(page.width, greaterThan(1000));
    expect(img.decodeJpg(page.jpeg)!.width, page.width);
  });

  test('a small page is never enlarged', () {
    final page = ScanProcessing.process(
      photo(400, 250),
      const ScanPageEdit(),
      widthMm: 85.6,
      heightMm: 53.98,
    );

    expect((page.width, page.height), (400, 250));
  });

  test('a quarter turn swaps the sides, and the crop keeps a part', () {
    final turned = ScanProcessing.process(
      photo(400, 200),
      const ScanPageEdit(quarterTurns: 1),
    );
    final cropped = ScanProcessing.process(
      photo(400, 200),
      const ScanPageEdit(crop: ScanCrop.rect(0.25, 0, 0.75, 0.5)),
    );

    expect((turned.width, turned.height), (200, 400));
    expect((cropped.width, cropped.height), (200, 100));
  });

  test('black and white leaves only black and white', () {
    final page = ScanProcessing.process(
      photo(64, 64),
      const ScanPageEdit(filter: ScanFilter.blackWhite),
    );
    final decoded = img.decodeJpg(page.jpeg)!;
    final centre = decoded.getPixel(32, 32);

    // JPEG blurs the edges a little; inside, a pixel is near 0 or 255.
    expect(centre.r < 40 || centre.r > 215, isTrue);
  });

  test('brightness lightens the page', () {
    final plain = img.decodeJpg(
      ScanProcessing.process(photo(64, 64), const ScanPageEdit()).jpeg,
    )!;
    final lighter = img.decodeJpg(
      ScanProcessing.process(
        photo(64, 64),
        const ScanPageEdit(brightness: 40),
      ).jpeg,
    )!;

    expect(lighter.getPixel(10, 40).g, greaterThan(plain.getPixel(10, 40).g));
  });

  test('a crop is kept inside the picture', () {
    final crop = const ScanCrop.rect(-0.2, 0.5, 0.52, 2).clamped();

    expect(crop.topLeft, (x: 0.0, y: 0.5));
    expect(crop.bottomRight, (x: 0.52, y: 1.0));
  });

  test('only a shape that can be straightened is usable: no crossing '
      'sides, no sliver (T-476)', () {
    expect(ScanCrop.whole.isUsable, isTrue);
    // A slanted page: every corner somewhere else.
    const slanted = ScanCrop(
      (x: 0.1, y: 0.2),
      (x: 0.8, y: 0.05),
      (x: 0.95, y: 0.9),
      (x: 0.05, y: 0.8),
    );
    expect(slanted.isUsable, isTrue);
    expect(slanted.isRect, isFalse);
    // The top right dragged past the bottom right: the sides cross.
    expect(ScanCrop.whole.withCorner(1, (x: 0.2, y: 1.2)).isUsable, isFalse);
    // Two corners on top of each other.
    expect(ScanCrop.whole.withCorner(1, (x: 0.02, y: 0.0)).isUsable, isFalse);
  });

  test('a slanted crop is straightened onto a flat page of its own size '
      '(T-476)', () {
    // A trapezium: the top 200 px across, the bottom 400, 200 px tall -
    // its slanted sides 224 px, so the flat page is 400 x 224.
    const trapezium = ScanCrop(
      (x: 0.25, y: 0.0),
      (x: 0.75, y: 0.0),
      (x: 1.0, y: 1.0),
      (x: 0.0, y: 1.0),
    );
    expect(trapezium.sizeIn(400, 200), (400, 224));

    final page = ScanProcessing.process(
      photo(400, 200),
      const ScanPageEdit(crop: trapezium),
    );
    expect((page.width, page.height), (400, 224));
  });

  test('bytes that are not a picture are refused', () {
    expect(
      () => ScanProcessing.process(
        Uint8List.fromList([1, 2, 3]),
        const ScanPageEdit(),
      ),
      throwsFormatException,
    );
  });
}
