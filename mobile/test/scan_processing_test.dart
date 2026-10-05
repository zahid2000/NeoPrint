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
      const ScanPageEdit(crop: ScanCrop(0.25, 0, 0.75, 0.5)),
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

  test('a crop is kept inside the picture and never collapses', () {
    final crop = const ScanCrop(-0.2, 0.5, 0.52, 2).clamped();

    expect(crop.left, 0);
    expect(crop.right, 0.52);
    expect(crop.bottom, 1);
    expect(
      const ScanCrop(0.9, 0.9, 0.9, 0.9).clamped().right,
      closeTo(0.95, 1e-9),
    );
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
