import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/storage/secure_storage.dart';
import 'package:neoprint/features/session/session_repository.dart';

import 'session_kit.dart';

SessionRepository _repository(SteppedAdapter adapter) {
  final dio = Dio(
    BaseOptions(baseUrl: 'http://backend.test/', validateStatus: (_) => true),
  )..httpClientAdapter = adapter;

  return SessionRepository(
    ApiClient(
      baseUrl: 'http://backend.test/',
      storage: InMemorySecureStorage(),
      tokens: AccessTokenHolder(),
      dio: dio,
    ),
  );
}

void main() {
  // The server says "not found" both of a session that is already over and
  // of one that is somebody else's (T-312): closing it is no error.
  test('closing a session that is already over is no error', () async {
    final adapter = SteppedAdapter()
      ..then('/api/sessions/s-1', (
        status: 404,
        body: {'title': 'Tapılmadı', 'code': 'notFound.session'},
      ));

    await expectLater(_repository(adapter).close('s-1'), completes);
  });

  test('any other refusal still reaches the screen', () async {
    final adapter = SteppedAdapter()
      ..then('/api/sessions/s-1', (
        status: 500,
        body: {'title': 'Gözlənilməz xəta baş verdi'},
      ));

    await expectLater(
      _repository(adapter).close('s-1'),
      throwsA(isA<ApiException>()),
    );
  });
}
