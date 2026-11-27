import 'package:dio/dio.dart';
import 'package:sentry_flutter/sentry_flutter.dart';

import '../network/api_base_url.dart';

/// Where the app reports its unhandled errors (T-478): the DSN the API hands
/// out, or null - error tracking off, or the API not reachable.
typedef ErrorTrackingConfig = ({String? dsn, String? environment});

/// Asks the API for the app's DSN; never throws.
Future<ErrorTrackingConfig> fetchErrorTrackingConfig({Dio? dio}) async {
  final client =
      dio ??
      Dio(
        BaseOptions(
          baseUrl: resolveApiBaseUrl(),
          connectTimeout: const Duration(seconds: 10),
          receiveTimeout: const Duration(seconds: 10),
        ),
      );
  try {
    final response = await client.get<Map<String, dynamic>>(
      'api/error-tracking/mobile',
    );
    final body = response.data ?? const {};
    final dsn = body['dsn'];
    final environment = body['environment'];
    return (
      dsn: dsn is String && dsn.isNotEmpty ? dsn : null,
      environment: environment is String ? environment : null,
    );
  } on Object {
    return (dsn: null, environment: null);
  }
}

/// Error tracking (plan step 8, T-478; docs/MONITORING.md): the app's
/// unhandled errors go to the self-hosted GlitchTip through the Sentry SDK.
///
/// Started after the app is already running, so a slow or missing API never
/// holds the first screen; without a DSN nothing is sent. What is sent is the
/// error and its stack - no user, no IP, no screenshot, no view tree, no
/// breadcrumbs of where the user went or what was fetched.
Future<bool> startErrorTracking({
  Future<ErrorTrackingConfig> Function()? config,
  Future<void> Function(FlutterOptionsConfiguration configure)? init,
}) async {
  final settings = await (config ?? fetchErrorTrackingConfig)();
  final dsn = settings.dsn;
  if (dsn == null) {
    return false;
  }

  await (init ?? SentryFlutter.init)((options) {
    options
      ..dsn = dsn
      ..environment = settings.environment
      ..sendDefaultPii = false
      ..tracesSampleRate = 0
      ..enableAutoSessionTracking = false
      ..attachScreenshot = false
      ..attachViewHierarchy = false
      ..beforeBreadcrumb = dropBreadcrumb
      ..beforeSend = scrub;
  });
  return true;
}

/// No breadcrumb leaves the phone: they record screens, taps and requests.
Breadcrumb? dropBreadcrumb(Breadcrumb? breadcrumb, Hint hint) => null;

/// Drops anything that could name the user or carry what they sent.
SentryEvent? scrub(SentryEvent event, Hint hint) {
  event
    ..user = null
    ..request = null;
  return event;
}
