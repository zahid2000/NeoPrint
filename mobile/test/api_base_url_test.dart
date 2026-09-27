import 'package:dio/dio.dart';
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
        withTrailingSlash(productionApiBaseUrl),
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
        'http://192.168.1.50:5080/',
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
        withTrailingSlash(androidEmulatorApiBaseUrl),
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
        withTrailingSlash(localApiBaseUrl),
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
        withTrailingSlash(localApiBaseUrl),
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
            isNot(withTrailingSlash(productionApiBaseUrl)),
            reason: 'platform $platform, web $isWeb',
          );
        }
      }
    });
    test('every target builds a valid request url', () {
      // The bug this guards against: without the trailing slash Dio produced
      // http://localhost:5080api/auth/register, which no browser or phone
      // could send, and registration failed as a "network error".
      final configurations = [
        for (final platform in TargetPlatform.values)
          for (final isWeb in [true, false])
            for (final isRelease in [true, false])
              resolveApiBaseUrl(
                configured: '',
                isRelease: isRelease,
                isWeb: isWeb,
                platform: platform,
              ),
        resolveApiBaseUrl(configured: 'http://192.168.1.50:5080'),
        resolveApiBaseUrl(configured: 'http://192.168.1.50:5080///'),
      ];

      for (final base in configurations) {
        final uri = RequestOptions(
          baseUrl: base,
          path: 'api/auth/register',
        ).uri;
        expect(uri.path, '/api/auth/register', reason: base);
        expect(uri.hasPort || uri.scheme == 'https', isTrue, reason: base);
      }
    });

    test('an address is given exactly one trailing slash', () {
      expect(
        withTrailingSlash('http://localhost:5080'),
        'http://localhost:5080/',
      );
      expect(
        withTrailingSlash('http://localhost:5080/'),
        'http://localhost:5080/',
      );
      expect(
        withTrailingSlash(' http://localhost:5080// '),
        'http://localhost:5080/',
      );
    });
  });
}
