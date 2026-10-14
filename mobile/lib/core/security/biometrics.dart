import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// The phone's fingerprint or face check (T-270), over our own channel - the
/// system's BiometricPrompt on Android, LocalAuthentication on iOS (plan
/// section 2.0: no library for it).
///
/// It answers one question - "is the owner of this phone here?" - and
/// unseals nothing: the PIN stays the way in.
abstract interface class Biometrics {
  /// Whether this phone can check a fingerprint or a face, with one enrolled.
  Future<bool> available();

  /// Shows the system's prompt; true when the check passed. [title] heads the
  /// prompt, [cancel] is its button back to the PIN.
  Future<bool> authenticate({required String title, required String cancel});
}

/// [Biometrics] over the platform channel.
final class PlatformBiometrics implements Biometrics {
  /// Implemented natively in android/ and ios/.
  static const _channel = MethodChannel('az.neoprint/biometrics');

  const PlatformBiometrics();

  @override
  Future<bool> available() async {
    try {
      return await _channel.invokeMethod<bool>('available') ?? false;
    } on MissingPluginException {
      // The web build: there is no fingerprint to ask for.
      return false;
    } on PlatformException {
      return false;
    }
  }

  @override
  Future<bool> authenticate({
    required String title,
    required String cancel,
  }) async {
    try {
      return await _channel.invokeMethod<bool>('authenticate', {
            'title': title,
            'cancel': cancel,
          }) ??
          false;
    } on MissingPluginException {
      return false;
    } on PlatformException {
      // The sensor would not start: the PIN is asked instead.
      return false;
    }
  }
}

final biometricsProvider = Provider<Biometrics>(
  (ref) => const PlatformBiometrics(),
);
