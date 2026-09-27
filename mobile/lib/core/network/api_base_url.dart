// The backend address for this build.
//
// A release build always talks to the real API. A debug build talks to the
// local Docker Compose stack, because that is the only backend a developer
// has: leaving the production address as the debug default meant every request
// failed in DNS and the screen only said "network error", which told nobody
// what was wrong (docs/DECISIONS.md).
//
// The address is not a secret; it is picked at build time so that the running
// app never has to be configured by hand.

import 'package:flutter/foundation.dart';

/// An explicit override: `--dart-define=NEOPRINT_API_URL=http://192.168.1.50:5080`.
///
/// This is how the staging and production builds differ, and how a real phone
/// on the same Wi-Fi reaches a developer's machine.
const _configuredApiUrl = String.fromEnvironment('NEOPRINT_API_URL');

/// Where a release build goes when nothing overrides it.
const productionApiBaseUrl = 'https://api.neoprint.az';

/// The Android emulator reaches the host machine on this address. `localhost`
/// inside the emulator is the emulator itself, so it would never find the API.
const androidEmulatorApiBaseUrl = 'http://10.0.2.2:5080';

/// The iOS simulator, a browser and the desktop shells all share the host's
/// loopback interface. 5080 is the port docker-compose publishes the API on.
const localApiBaseUrl = 'http://localhost:5080';

/// The address with exactly one trailing slash.
///
/// Every request path is relative (`api/auth/register`), and Dio joins it to
/// the base address as written: without the slash the result was
/// `http://localhost:5080api/auth/register`, a URL with an invalid port. The
/// browser refused it before sending anything, so the API never saw a request
/// and the screen said "network error" - on every target, not just the web
/// (docs/DECISIONS.md).
String withTrailingSlash(String url) {
  var trimmed = url.trim();
  while (trimmed.endsWith('/')) {
    trimmed = trimmed.substring(0, trimmed.length - 1);
  }
  return '$trimmed/';
}

/// Resolves the address this build should use.
///
/// The parameters exist so the decision can be tested for every target without
/// building the app once per platform.
String resolveApiBaseUrl({
  String configured = _configuredApiUrl,
  bool isRelease = kReleaseMode,
  bool isWeb = kIsWeb,
  TargetPlatform? platform,
}) => withTrailingSlash(
  _pickApiBaseUrl(
    configured: configured,
    isRelease: isRelease,
    isWeb: isWeb,
    platform: platform,
  ),
);

String _pickApiBaseUrl({
  required String configured,
  required bool isRelease,
  required bool isWeb,
  TargetPlatform? platform,
}) {
  if (configured.isNotEmpty) {
    return configured;
  }

  if (isRelease) {
    return productionApiBaseUrl;
  }

  // Web runs in a browser even on an Android phone, so the platform only
  // decides the address when the app is not running in one.
  if (!isWeb && (platform ?? defaultTargetPlatform) == TargetPlatform.android) {
    return androidEmulatorApiBaseUrl;
  }

  return localApiBaseUrl;
}
