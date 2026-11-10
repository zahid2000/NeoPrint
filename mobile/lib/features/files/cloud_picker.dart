import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// The cloud apps whose own file chooser "Buluddan əlavə et" opens (T-399),
/// in the order they are offered.
enum CloudApp {
  googleDrive('com.google.android.apps.docs', 'Google Drive'),
  oneDrive('com.microsoft.skydrive', 'OneDrive'),
  dropbox('com.dropbox.android', 'Dropbox');

  const CloudApp(this.package, this.title);

  /// The Android app's id - what the platform side opens.
  final String package;

  /// The product's own name, the same in every language.
  final String title;
}

/// A file chosen in a cloud app, copied to the phone.
typedef CloudFile = ({String path, String name});

/// Why a cloud file did not come.
enum CloudPickFailure {
  /// Over the phone's own limit for a copy (far above any upload limit).
  tooLarge,

  /// The app could not be opened, or the file not read.
  failed,
}

final class CloudPickException implements Exception {
  const CloudPickException(this.failure);
  final CloudPickFailure failure;
}

/// Opens a cloud app's own chooser (`CloudPicker.kt`, Android only). No
/// library: the app on the phone works with the user's own account.
class CloudPicker {
  const CloudPicker();

  static const _channel = MethodChannel('az.neoprint/cloud_picker');

  /// The cloud apps on this phone whose chooser can open; none off Android.
  Future<List<CloudApp>> installed() async {
    if (kIsWeb || defaultTargetPlatform != TargetPlatform.android) {
      return const [];
    }
    final packages = await _channel.invokeListMethod<String>('installed');
    return [
      for (final app in CloudApp.values)
        if (packages?.contains(app.package) ?? false) app,
    ];
  }

  /// The file chosen in [app]'s chooser; null when it was closed.
  Future<CloudFile?> pick(CloudApp app) async {
    try {
      final file = await _channel.invokeMapMethod<String, String>('pick', {
        'app': app.package,
      });
      if (file == null) {
        return null;
      }
      return (path: file['path']!, name: file['name']!);
    } on PlatformException catch (error) {
      throw CloudPickException(
        error.code == 'TOO_LARGE'
            ? CloudPickFailure.tooLarge
            : CloudPickFailure.failed,
      );
    }
  }
}

final cloudPickerProvider = Provider<CloudPicker>((_) => const CloudPicker());
