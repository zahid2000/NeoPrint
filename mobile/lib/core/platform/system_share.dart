import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Whether this build has a system share sheet: Android and iOS do, the web
/// build does not (docs/DESIGN_SYSTEM.md, "Mobil: dostunu dəvət et" - the
/// button there copies the link instead).
final canShareProvider = Provider<bool>(
  (_) =>
      !kIsWeb &&
      (defaultTargetPlatform == TargetPlatform.android ||
          defaultTargetPlatform == TargetPlatform.iOS),
);

final systemShareProvider = Provider<SystemShare>((_) => const SystemShare());

/// Text handed to the phone's own share sheet through our platform channel
/// (plan sections 2.0, 8.2): Android `ACTION_SEND`, iOS
/// `UIActivityViewController`. No plugin.
class SystemShare {
  const SystemShare();

  static const _channel = MethodChannel('az.neoprint/share');

  /// Opens the share sheet with [text]. False when the phone has nothing to
  /// share with, or the build has no sheet at all.
  Future<bool> text(String text, {String? subject}) async {
    try {
      return await _channel.invokeMethod<bool>('text', {
            'text': text,
            if (subject != null) 'subject': subject,
          }) ??
          false;
    } on PlatformException {
      return false;
    } on MissingPluginException {
      return false;
    }
  }
}
