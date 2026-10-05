import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

/// Why the scanner did not give pages back.
enum ScannerFailure {
  /// No scanner on this device: the web build, Google Play services missing
  /// or too old, or an iPhone that cannot scan.
  unavailable,

  /// Something went wrong while scanning.
  failed,
}

final class ScannerException implements Exception {
  const ScannerException(this.reason);

  final ScannerFailure reason;

  @override
  String toString() => 'ScannerException($reason)';
}

/// The phone's own document scanner (plan section 2.11): Google ML Kit's
/// Document Scanner on Android, VisionKit on iOS - edge detection,
/// perspective correction and several pages in one go. The native code is
/// in DocumentScanner.kt and AppDelegate.swift.
class DocumentScanner {
  const DocumentScanner({MethodChannel? channel})
    : _channel = channel ?? const MethodChannel(channelName);

  static const channelName = 'az.neoprint/document_scanner';

  /// Most pages one scan takes; the backend accepts up to 50 pictures.
  static const maxPages = 20;

  final MethodChannel _channel;

  /// Whether this build can scan at all: not on the web, where the entry
  /// point is hidden rather than shown and refused.
  static bool get isSupportedPlatform =>
      !kIsWeb &&
      (defaultTargetPlatform == TargetPlatform.android ||
          defaultTargetPlatform == TargetPlatform.iOS);

  /// Opens the scanner and returns the pages' JPEG paths in the app's cache,
  /// in order. Empty when the user closed it without scanning.
  Future<List<String>> scan({int pageLimit = 10}) async {
    if (!isSupportedPlatform) {
      throw const ScannerException(ScannerFailure.unavailable);
    }

    try {
      final paths = await _channel.invokeListMethod<String>('scan', {
        'pageLimit': pageLimit.clamp(1, maxPages),
      });

      return paths ?? const [];
    } on PlatformException catch (error) {
      throw ScannerException(
        error.code == 'UNAVAILABLE'
            ? ScannerFailure.unavailable
            : ScannerFailure.failed,
      );
    } on MissingPluginException {
      throw const ScannerException(ScannerFailure.unavailable);
    }
  }
}
