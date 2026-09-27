import 'package:flutter/services.dart';

/// Why a HEIC photo could not be turned into a JPEG.
enum HeicFailure {
  /// The phone cannot read HEIC at all (Android before 9).
  unsupported,

  /// The file could not be read or written.
  failed,
}

final class HeicConversionException implements Exception {
  const HeicConversionException(this.reason);

  final HeicFailure reason;

  @override
  String toString() => 'HeicConversionException($reason)';
}

/// Turns a HEIC/HEIF photo into a JPEG with the phone's own image API
/// (plan section 2.2: the backend does not accept HEIC; the phone converts it
/// before upload). Android uses `ImageDecoder`, iOS `UIImage`; both apply the
/// photo's orientation. The code is in MainActivity.kt and AppDelegate.swift.
class HeicConverter {
  const HeicConverter({MethodChannel? channel})
    : _channel = channel ?? const MethodChannel(channelName);

  static const channelName = 'az.neoprint/heic';

  final MethodChannel _channel;

  /// Writes a JPEG next to the app's cache and returns its path.
  Future<String> toJpeg(String path) async {
    try {
      final result = await _channel.invokeMethod<String>('toJpeg', {
        'path': path,
      });

      if (result == null || result.isEmpty) {
        throw const HeicConversionException(HeicFailure.failed);
      }

      return result;
    } on PlatformException catch (error) {
      throw HeicConversionException(
        error.code == 'UNSUPPORTED'
            ? HeicFailure.unsupported
            : HeicFailure.failed,
      );
    } on MissingPluginException {
      // The web build, or a platform without the channel.
      throw const HeicConversionException(HeicFailure.unsupported);
    }
  }
}
