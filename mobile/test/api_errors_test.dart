import 'dart:ui';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/l10n/app_language.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/storage/secure_storage.dart';
import 'package:neoprint/l10n/app_localizations.dart';
import 'package:neoprint/shared/api_error_text.dart';

/// Answers every request with the given status and records the headers.
final class _RecordingAdapter implements HttpClientAdapter {
  _RecordingAdapter(
    this.status,
    this.body, {
    this.contentType = Headers.jsonContentType,
  });

  final int status;
  final String body;
  final String contentType;
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
        Headers.contentTypeHeader: [contentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

/// No connection at all.
final class _UnreachableAdapter implements HttpClientAdapter {
  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<List<int>>? requestStream,
    Future<void>? cancelFuture,
  ) => throw DioException.connectionError(
    requestOptions: options,
    reason: 'No connection',
  );

  @override
  void close({bool force = false}) {}
}

ApiClient _client(HttpClientAdapter adapter, {String Function()? language}) {
  final dio = Dio(
    BaseOptions(baseUrl: 'http://backend.test/', validateStatus: (_) => true),
  )..httpClientAdapter = adapter;

  return ApiClient(
    baseUrl: 'http://backend.test/',
    storage: InMemorySecureStorage(),
    tokens: AccessTokenHolder(),
    language: language,
    dio: dio,
  );
}

Future<ApiException> _failure(ApiClient api) async {
  try {
    await api.send<void>(
      method: 'POST',
      path: 'api/auth/register',
      authenticated: false,
      parse: (_) {},
    );
  } on ApiException catch (error) {
    return error;
  }
  fail('The request was expected to fail.');
}

void main() {
  test('the app language goes to the server as Accept-Language', () async {
    // So the server's own sentences - validation, refusals, emails - come
    // back in the language the screen is in.
    final adapter = _RecordingAdapter(200, '{}');

    await _client(
      adapter,
      language: () => 'en',
    ).send<void>(method: 'GET', path: 'api/files', parse: (_) {});

    expect(adapter.sentHeaders.single['Accept-Language'], 'en');
  });

  test('a request that got no answer says the server could not be reached, '
      'in the language of the screen', () async {
    final error = await _failure(_client(_UnreachableAdapter()));

    expect(error.kind, ApiErrorKind.unreachable);
    expect(
      apiErrorText(lookupAppLocalizations(const Locale('az')), error),
      startsWith('Serverə qoşulmaq mümkün olmadı'),
    );
    expect(
      apiErrorText(lookupAppLocalizations(const Locale('en')), error),
      startsWith('Could not connect to the server'),
    );
  });

  test('a refused request for bytes still carries the server words', () async {
    // A page image that could not be drawn: the answer is a problem document,
    // but it arrives as bytes because bytes were asked for.
    const body =
        '{"title":"Sənəd emalı xidməti cavab vermir",'
        '"detail":"Sənəd emalı xidməti hazırda işləmir. Bir az sonra yenidən cəhd edin.",'
        '"code":"files.processingUnavailable"}';

    final api = _client(
      _RecordingAdapter(
        503,
        body,
        contentType: 'application/problem+json; charset=utf-8',
      ),
    );

    ApiException? error;

    try {
      await api.send<List<int>?>(
        method: 'GET',
        path: 'api/files/1/pages/1.png',
        responseType: ResponseType.bytes,
        parse: (data) => data as List<int>?,
      );
    } on ApiException catch (caught) {
      error = caught;
    }

    expect(error?.statusCode, 503);
    expect(error?.code, 'files.processingUnavailable');
    expect(
      apiErrorText(lookupAppLocalizations(const Locale('az')), error!),
      startsWith('Sənəd emalı xidməti hazırda işləmir'),
    );
  });

  test('a refusal is shown in the server words', () async {
    final error = await _failure(
      _client(
        _RecordingAdapter(400, '{"title":"x","detail":"Bu email artıq var."}'),
      ),
    );

    expect(
      apiErrorText(lookupAppLocalizations(const Locale('en')), error),
      'Bu email artıq var.',
    );
  });

  test(
    'a server error without a body is worded from the translations',
    () async {
      final error = await _failure(
        _client(
          _RecordingAdapter(
            502,
            '<html>Bad gateway</html>',
            contentType: 'text/html',
          ),
        ),
      );

      expect(error.kind, ApiErrorKind.server);
      expect(
        apiErrorText(lookupAppLocalizations(const Locale('ru')), error),
        lookupAppLocalizations(const Locale('ru')).errorServer,
      );
    },
  );

  test('the app language is the device one when we have it, else az', () {
    expect(resolveLanguageCode(const Locale('en', 'US')), 'en');
    expect(resolveLanguageCode(const Locale('ru')), 'ru');
    expect(resolveLanguageCode(const Locale('de')), 'az');
    expect(resolveLanguageCode(null), 'az');
  });
}
