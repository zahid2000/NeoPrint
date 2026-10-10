import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/storage/secure_storage.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  const channel = MethodChannel('az.neoprint/secure_storage');
  const storage = PlatformSecureStorage();

  void handle(Future<Object?> Function(MethodCall call)? handler) {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, handler);
  }

  tearDown(() => handle(null));

  test('speaks the channel the native side answers on (T-195)', () async {
    final values = <String, String>{};
    final calls = <String>[];

    handle((call) async {
      calls.add(call.method);
      final arguments =
          (call.arguments as Map<Object?, Object?>?) ??
          const <Object?, Object?>{};
      final key = arguments['key'] as String?;

      switch (call.method) {
        case 'read':
          return values[key];
        case 'write':
          values[key!] = arguments['value']! as String;
        case 'delete':
          values.remove(key);
        case 'clear':
          values.clear();
      }

      return null;
    });

    expect(await storage.read(SecureStorageKeys.refreshToken), isNull);

    await storage.write(SecureStorageKeys.refreshToken, 'token-1');
    await storage.write(SecureStorageKeys.deviceId, 'device-1');
    expect(await storage.read(SecureStorageKeys.refreshToken), 'token-1');

    // Signing out removes the token and keeps the per-install id.
    await storage.delete(SecureStorageKeys.refreshToken);
    expect(await storage.read(SecureStorageKeys.refreshToken), isNull);
    expect(await storage.read(SecureStorageKeys.deviceId), 'device-1');

    await storage.clear();
    expect(values, isEmpty);
    expect(calls, containsAll(['read', 'write', 'delete', 'clear']));
  });

  test('no native side reads as nothing stored', () async {
    expect(await storage.read(SecureStorageKeys.refreshToken), isNull);
    await storage.write(SecureStorageKeys.refreshToken, 'token-1');
    await storage.delete(SecureStorageKeys.refreshToken);
    await storage.clear();
  });

  test('a key store that refuses does not break sign-in', () async {
    handle((call) async => throw PlatformException(code: 'FAILED'));

    expect(await storage.read(SecureStorageKeys.refreshToken), isNull);
    await storage.write(SecureStorageKeys.refreshToken, 'token-1');
  });
}
