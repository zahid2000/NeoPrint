import 'dart:io';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:neoprint/core/app_version.dart';
import 'package:neoprint/core/l10n/app_delegates.dart';
import 'package:neoprint/core/l10n/app_language.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/network/update_required.dart';
import 'package:neoprint/core/storage/secure_storage.dart';
import 'package:neoprint/features/update/update_required_screen.dart';
import 'package:neoprint/l10n/app_localizations.dart';

/// Answers every request with the given status and records the headers.
final class _RecordingAdapter implements HttpClientAdapter {
  _RecordingAdapter(this.status, this.body);

  final int status;
  final String body;
  final List<Map<String, dynamic>> sentHeaders = [];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<List<int>>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    sentHeaders.add(Map<String, dynamic>.from(options.headers));
    return ResponseBody.fromString(
      body,
      status,
      headers: {
        Headers.contentTypeHeader: ['application/problem+json'],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

ApiClient _client(HttpClientAdapter adapter) {
  final dio = Dio(
    BaseOptions(baseUrl: 'http://backend.test/', validateStatus: (_) => true),
  )..httpClientAdapter = adapter;

  return ApiClient(
    baseUrl: 'http://backend.test/',
    storage: InMemorySecureStorage(),
    tokens: AccessTokenHolder(),
    dio: dio,
  );
}

/// The backend no longer serves an app this old (docs/adr/ADR-0009, T-492).
void main() {
  test('the version the app reports is the one pubspec.yaml builds', () {
    final pubspec = File('pubspec.yaml').readAsLinesSync();
    final line = pubspec.firstWhere((l) => l.startsWith('version:'));
    final version = line.substring('version:'.length).trim().split('+').first;

    expect(appVersion, version);
  });

  test('every request says the app version', () async {
    final adapter = _RecordingAdapter(200, '{}');

    await _client(adapter)
        .send<void>(method: 'GET', path: 'api/wallet', parse: (_) {});

    expect(adapter.sentHeaders.single[appVersionHeader], appVersion);
  });

  test('a 426 from the server asks for an update', () async {
    final api = _client(
      _RecordingAdapter(
        426,
        '{"status":426,"title":"Tətbiqi yeniləyin","code":"app.updateRequired","minVersion":"2.0.0"}',
      ),
    );
    var asked = 0;
    api.onUpdateRequired = () => asked++;

    await expectLater(
      api.send<void>(method: 'GET', path: 'api/wallet', parse: (_) {}),
      throwsA(
        isA<ApiException>().having(
          (e) => e.isUpdateRequired,
          'isUpdateRequired',
          isTrue,
        ),
      ),
    );
    expect(asked, 1);
  });

  test('another refusal does not', () async {
    final api = _client(
      _RecordingAdapter(409, '{"status":409,"code":"wallet.insufficient"}'),
    );
    var asked = 0;
    api.onUpdateRequired = () => asked++;

    await expectLater(
      api.send<void>(method: 'GET', path: 'api/wallet', parse: (_) {}),
      throwsA(isA<ApiException>()),
    );
    expect(asked, 0);
  });

  for (final code in supportedLanguageCodes) {
    testWidgets('the update screen says what to do and lets the app check '
        'again ($code)', (tester) async {
      final container = ProviderContainer();
      addTearDown(container.dispose);
      container.read(updateRequiredProvider.notifier).state = true;

      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: container,
          child: MaterialApp(
            locale: Locale(code),
            localizationsDelegates: appLocalizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: const UpdateRequiredScreen(),
          ),
        ),
      );

      final l10n = lookupAppLocalizations(Locale(code));
      expect(find.text(l10n.updateRequiredTitle), findsOneWidget);
      expect(find.text(l10n.updateRequiredBody(appVersion)), findsOneWidget);

      await tester.tap(find.byKey(const Key('update-required-retry')));
      expect(container.read(updateRequiredProvider), isFalse);
    });
  }
}
