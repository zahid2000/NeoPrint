import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/error_tracking/error_tracking.dart';
import 'package:sentry_flutter/sentry_flutter.dart';

/// Error tracking (T-478): started only with a DSN from the API, and what it
/// sends names nobody.
void main() {
  test('starts only when the API hands out a DSN', () async {
    final started = <SentryFlutterOptions>[];
    Future<void> init(FlutterOptionsConfiguration configure) async {
      final options = SentryFlutterOptions();
      await configure(options);
      started.add(options);
    }

    expect(
      await startErrorTracking(
        config: () async => (dsn: null, environment: 'Development'),
        init: init,
      ),
      isFalse,
    );
    expect(started, isEmpty);

    expect(
      await startErrorTracking(
        config: () async =>
            (dsn: 'https://key@errors.example.test/3', environment: 'Test'),
        init: init,
      ),
      isTrue,
    );
    final options = started.single;
    expect(options.dsn, 'https://key@errors.example.test/3');
    expect(options.environment, 'Test');
    expect(options.sendDefaultPii, isFalse);
    expect(options.attachScreenshot, isFalse);
    expect(options.attachViewHierarchy, isFalse);
  });

  test('sends the error, not who had it or what they sent', () {
    final event = SentryEvent(
      user: SentryUser(email: 'someone@example.test', ipAddress: '203.0.113.7'),
      request: SentryRequest(url: 'https://api.example.test/api/files?q=x'),
    );

    final sent = scrub(event, Hint())!;

    expect(sent.user, isNull);
    expect(sent.request, isNull);
    expect(
      dropBreadcrumb(Breadcrumb(message: 'opened /files'), Hint()),
      isNull,
    );
  });
}
