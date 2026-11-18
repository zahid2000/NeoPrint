import 'package:flutter/foundation.dart';
import 'package:image/image.dart' as img;

import '../../core/network/api_client.dart';
import '../../core/network/idempotency.dart';

/// A picture chosen for a claim (T-438), already shrunk; its key stays with
/// it, so a retry after a lost answer does not add it twice.
@immutable
final class ClaimPhotoDraft {
  ClaimPhotoDraft(this.jpeg) : idempotencyKey = newIdempotencyKey();

  final Uint8List jpeg;
  final String idempotencyKey;
}

/// What sending a claim came to: its own failure, or how many of its
/// pictures did not go.
@immutable
final class ClaimReport {
  const ClaimReport({this.error, this.photosFailed = 0});

  final ApiException? error;
  final int photosFailed;
}

/// The longest side a claim picture keeps: enough to see a jam or a smudge.
const claimPhotoMaxSide = 1600;

/// A phone picture as a JPEG of at most [claimPhotoMaxSide] (T-438), off the
/// UI thread; null when the bytes are not a picture this can read.
Future<Uint8List?> shrinkClaimPhoto(Uint8List bytes) => compute(_shrink, bytes);

Uint8List? _shrink(Uint8List bytes) {
  img.Image? decoded;
  try {
    decoded = img.decodeImage(bytes);
  } on Exception {
    decoded = null;
  } on RangeError {
    // Not a picture: some decoders run off the end of what they were given.
    decoded = null;
  }
  if (decoded == null) {
    return null;
  }

  final oriented = img.bakeOrientation(decoded);
  final longest = oriented.width > oriented.height
      ? oriented.width
      : oriented.height;
  final sized = longest > claimPhotoMaxSide
      ? img.copyResize(
          oriented,
          width: oriented.width >= oriented.height ? claimPhotoMaxSide : null,
          height: oriented.height > oriented.width ? claimPhotoMaxSide : null,
          interpolation: img.Interpolation.average,
        )
      : oriented;

  return img.encodeJpg(sized, quality: 85);
}
