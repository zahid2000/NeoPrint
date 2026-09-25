import 'package:flutter/foundation.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/network/api_base_url.dart';

void main() {
  group('resolveApiBaseUrl', () {
    test('a release build talks to the real API', () {
      expect(
        resolveApiBaseUrl(
          configured: '',
          isRelease: true,
          isWeb: false,
          platform: TargetPlatform.android,
        ),
        productionApiBaseUrl,
      );
    });

    test('an explicit address wins over everything else', () {
      expect(
        resolveApiBaseUrl(
          configured: 'http://192.168.1.50:5080',
          isRelease: true,
          isWeb: false,
          platform: TargetPlatform.android,
        ),
        'http://192.168.1.50:5080',
      );
    });

    test('a debug build on Android reaches the host through 10.0.2.2', () {
      expect(
        resolveApiBaseUrl(
          configured: '',
          isRelease: false,
          isWeb: false,
          platform: TargetPlatform.android,
        ),
        androidEmulatorApiBaseUrl,
      );
    });

    test('a debug build in a browser uses the loopback address', () {
      // Chrome on an Android host still reports TargetPlatform.android, so the
      // browser has to win over the platform or the request would go to an
      // address the browser cannot reach.
      expect(
        resolveApiBaseUrl(
          configured: '',
          isRelease: false,
          isWeb: true,
          platform: TargetPlatform.android,
        ),
        localApiBaseUrl,
      );
    });

    test('a debug build on iOS uses the loopback address', () {
      expect(
        resolveApiBaseUrl(
          configured: '',
          isRelease: false,
          isWeb: false,
          platform: TargetPlatform.iOS,
        ),
        localApiBaseUrl,
      );
    });

    test('no debug build is ever pointed at production by accident', () {
      for (final platform in TargetPlatform.values) {
        for (final isWeb in [true, false]) {
          expect(
            resolveApiBaseUrl(
              configured: '',
              isRelease: false,
              isWeb: isWeb,
              platform: platform,
            ),
            isNot(productionApiBaseUrl),
            reason: 'platform $platform, web $isWeb',
          );
        }
      }
    });
  });
}
