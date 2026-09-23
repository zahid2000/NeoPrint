import 'package:flutter/services.dart';

/// Secure storage for the refresh token, over our own platform channel.
///
/// Plan section 2.0 rules out a storage package: the channel talks straight to
/// the Android Keystore and the iOS Keychain. Plan section 2.8 requires the
/// refresh token to live there and nowhere else - it is the one credential on
/// the device that can mint new access tokens.
///
/// Nothing else is kept here. Access tokens live for fifteen minutes and stay
/// in memory; putting them in the keychain would only slow sign-in down.
abstract interface class SecureStorage {
  Future<String?> read(String key);

  Future<void> write(String key, String value);

  Future<void> delete(String key);

  /// Clears everything. Used when signing out and when the server rejects the
  /// refresh token, which means it is no longer worth keeping.
  Future<void> clear();
}

/// Keys the application stores.
abstract final class SecureStorageKeys {
  /// The refresh token (plan section 2.8).
  static const refreshToken = 'refresh_token';

  /// A random per-install identifier, used for the self-referral and
  /// repeat-referral check (plan section 2.6).
  static const deviceId = 'device_id';
}

/// [SecureStorage] over the platform channel.
final class PlatformSecureStorage implements SecureStorage {
  /// Implemented natively in android/ and ios/ (plan section 2.0).
  static const _channel = MethodChannel('az.neoprint/secure_storage');

  const PlatformSecureStorage();

  @override
  Future<String?> read(String key) async {
    try {
      return await _channel.invokeMethod<String>('read', {'key': key});
    } on MissingPluginException {
      // The native side is not wired up on this platform yet. Reporting
      // "nothing stored" is right: it makes the app ask the user to sign in
      // rather than crash, and it never invents a token.
      return null;
    }
  }

  @override
  Future<void> write(String key, String value) async {
    try {
      await _channel.invokeMethod<void>('write', {'key': key, 'value': value});
    } on MissingPluginException {
      // Deliberately swallowed rather than rethrown: a device that cannot
      // store the token still works, it just asks for the password again next
      // time. Failing sign-in outright would be worse.
    }
  }

  @override
  Future<void> delete(String key) async {
    try {
      await _channel.invokeMethod<void>('delete', {'key': key});
    } on MissingPluginException {
      // Nothing was stored, so nothing to remove.
    }
  }

  @override
  Future<void> clear() async {
    try {
      await _channel.invokeMethod<void>('clear');
    } on MissingPluginException {
      // As above.
    }
  }
}

/// In-memory storage, for tests and for the widget previews.
final class InMemorySecureStorage implements SecureStorage {
  final Map<String, String> _values = {};

  /// What is currently held, for assertions in tests.
  Map<String, String> get values => Map.unmodifiable(_values);

  @override
  Future<String?> read(String key) async => _values[key];

  @override
  Future<void> write(String key, String value) async => _values[key] = value;

  @override
  Future<void> delete(String key) async => _values.remove(key);

  @override
  Future<void> clear() async => _values.clear();
}
