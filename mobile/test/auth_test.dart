import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/auth/auth_models.dart';
import 'package:neoprint/core/auth/auth_repository.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/network/correlation_id.dart';
import 'package:neoprint/core/storage/secure_storage.dart';

/// A scripted transport, so the flows are exercised without a backend.
///
/// Plan section 11 rules out a mocking library, so this is written by hand.
final class ScriptedAdapter implements HttpClientAdapter {
  final Map<String, List<_ScriptedResponse>> _responses = {};

  /// Every request that was made, in order.
  final List<String> requests = [];

  /// The headers each of those requests carried, in the same order.
  final List<Map<String, dynamic>> sentHeaders = [];

  void respond(String path, int statusCode, {Map<String, dynamic>? body}) {
    _responses
        .putIfAbsent(path, () => [])
        .add(_ScriptedResponse(statusCode, body));
  }

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<List<int>>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    final path = options.uri.path;
    requests.add('${options.method} $path');
    sentHeaders.add(Map<String, dynamic>.from(options.headers));

    final queue = _responses[path];

    if (queue == null || queue.isEmpty) {
      throw StateError('No scripted response for ${options.method} $path');
    }

    final scripted = queue.removeAt(0);

    return ResponseBody.fromString(
      scripted.body == null ? '{}' : _encode(scripted.body!),
      scripted.statusCode,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}

  static String _encode(Map<String, dynamic> body) {
    // A tiny encoder, so the test has no dependency of its own.
    String encodeValue(dynamic value) {
      if (value == null) return 'null';
      if (value is num || value is bool) return value.toString();
      if (value is String) return '"${value.replaceAll('"', r'\"')}"';
      if (value is List) return '[${value.map(encodeValue).join(',')}]';
      if (value is Map) {
        return '{${value.entries.map((e) => '"${e.key}":${encodeValue(e.value)}').join(',')}}';
      }
      throw ArgumentError('Cannot encode ${value.runtimeType}');
    }

    return encodeValue(body);
  }
}

final class _ScriptedResponse {
  const _ScriptedResponse(this.statusCode, this.body);

  final int statusCode;
  final Map<String, dynamic>? body;
}

Map<String, dynamic> sessionBody({
  List<Map<String, dynamic>> pendingConsents = const [],
  String refreshToken = 'refresh-1',
}) => {
  'accessToken': 'access-1',
  'accessTokenExpiresAt': DateTime.now()
      .toUtc()
      .add(const Duration(minutes: 15))
      .toIso8601String(),
  'refreshToken': refreshToken,
  'user': {
    'id': 'a1b2c3',
    'publicId': 'NP-ABC234',
    'email': 'user@example.com',
    'fullName': 'Test User',
    'language': 'az',
    'emailConfirmed': true,
    'pendingConsents': pendingConsents,
  },
};

void main() {
  late ScriptedAdapter adapter;
  late InMemorySecureStorage storage;
  late AccessTokenHolder tokens;
  late AuthRepository repository;

  setUp(() {
    adapter = ScriptedAdapter();
    storage = InMemorySecureStorage();
    tokens = AccessTokenHolder();

    final dio = Dio(
      BaseOptions(baseUrl: 'http://backend.test/', validateStatus: (_) => true),
    )..httpClientAdapter = adapter;

    final api = ApiClient(
      baseUrl: 'http://backend.test/',
      storage: storage,
      tokens: tokens,
      dio: dio,
    );

    repository = AuthRepository(api: api, storage: storage, tokens: tokens);
  });

  group('registration', () {
    test('does not sign anyone in', () async {
      adapter.respond(
        '/api/auth/register',
        200,
        body: {
          'userId': 'a1b2c3',
          'email': 'user@example.com',
          'confirmationRequired': true,
        },
      );

      final email = await repository.register(
        email: 'user@example.com',
        password: 'Password123',
        consents: const [
          PendingConsent(
            docType: LegalDocumentType.privacy,
            version: '0.1-draft',
          ),
          PendingConsent(
            docType: LegalDocumentType.terms,
            version: '0.1-draft',
          ),
        ],
      );

      expect(email, 'user@example.com');

      // Plan section 2.8: no token until the email is confirmed.
      expect(tokens.hasToken, isFalse);
      expect(storage.values[SecureStorageKeys.refreshToken], isNull);
    });

    test('generates a device id once and then reuses it', () async {
      adapter
        ..respond('/api/auth/register', 200, body: {'email': 'a@example.com'})
        ..respond('/api/auth/register', 200, body: {'email': 'a@example.com'});

      await repository.register(
        email: 'a@example.com',
        password: 'Password123',
        consents: const [],
      );

      final first = storage.values[SecureStorageKeys.deviceId];
      expect(first, isNotNull);
      expect(first, hasLength(32));

      await repository.register(
        email: 'a@example.com',
        password: 'Password123',
        consents: const [],
      );

      // Plan section 2.6: a stable identifier is what makes the repeat-referral
      // check work at all.
      expect(storage.values[SecureStorageKeys.deviceId], first);
    });
  });

  group('confirming the email', () {
    test('signs the user in and stores the refresh token', () async {
      adapter.respond('/api/auth/confirm-email', 200, body: sessionBody());

      final session = await repository.confirmEmail(
        email: 'user@example.com',
        code: '123456',
      );

      expect(session.user.publicId, 'NP-ABC234');
      expect(tokens.hasToken, isTrue);

      // Plan section 2.8: the refresh token belongs in secure storage and
      // nowhere else.
      expect(storage.values[SecureStorageKeys.refreshToken], 'refresh-1');
    });

    test('a rejected code leaves no session behind', () async {
      adapter.respond(
        '/api/auth/confirm-email',
        400,
        body: {
          'title': 'Validation failed',
          'errors': {
            'code': ['Kod yanlışdır və ya vaxtı keçib.'],
          },
        },
      );

      await expectLater(
        repository.confirmEmail(email: 'user@example.com', code: '000000'),
        throwsA(
          isA<ApiException>().having(
            (error) => error.errorFor('code'),
            'code error',
            'Kod yanlışdır və ya vaxtı keçib.',
          ),
        ),
      );

      expect(tokens.hasToken, isFalse);
      expect(storage.values[SecureStorageKeys.refreshToken], isNull);
    });
  });

  group('signing in', () {
    test('reports pending consents so the router can gate on them', () async {
      adapter.respond(
        '/api/auth/login',
        200,
        body: sessionBody(
          pendingConsents: [
            {'docType': 'Terms', 'version': '0.2'},
          ],
        ),
      );

      final session = await repository.signIn(
        email: 'user@example.com',
        password: 'Password123',
      );

      // Plan section 2.8: a new version of a document is accepted again at the
      // next sign-in.
      expect(session.user.hasPendingConsents, isTrue);
      expect(
        session.user.pendingConsents.single.docType,
        LegalDocumentType.terms,
      );
      expect(session.user.pendingConsents.single.version, '0.2');
    });

    test('surfaces the server message rather than inventing one', () async {
      adapter.respond(
        '/api/auth/login',
        400,
        body: {
          'title': 'Validation failed',
          'errors': {
            '': ['Email və ya şifrə yanlışdır.'],
          },
        },
      );

      await expectLater(
        repository.signIn(email: 'user@example.com', password: 'wrong'),
        throwsA(
          isA<ApiException>().having(
            (error) => error.message,
            'message',
            'Email və ya şifrə yanlışdır.',
          ),
        ),
      );
    });
  });

  group('restoring a session', () {
    test('returns nothing on a fresh install', () async {
      expect(await repository.restore(), isNull);

      // No request should have been made: there was nothing to restore.
      expect(adapter.requests, isEmpty);
    });

    test('exchanges a stored token and rotates it', () async {
      await storage.write(SecureStorageKeys.refreshToken, 'old-refresh');

      adapter.respond(
        '/api/auth/refresh',
        200,
        body: sessionBody(refreshToken: 'new-refresh'),
      );

      final session = await repository.restore();

      expect(session, isNotNull);

      // Plan section 2.8: the token rotates, so the stored one has to be
      // replaced or the next refresh looks like a replay.
      expect(storage.values[SecureStorageKeys.refreshToken], 'new-refresh');
    });

    test('discards a token the server refuses', () async {
      await storage.write(SecureStorageKeys.refreshToken, 'stale');

      adapter.respond(
        '/api/auth/refresh',
        400,
        body: {
          'title': 'Validation failed',
          'errors': {
            'refreshToken': ['Sessiya bitib. Yenidən daxil olun.'],
          },
        },
      );

      expect(await repository.restore(), isNull);

      // Keeping a refused token would make every start fail the same way.
      expect(storage.values[SecureStorageKeys.refreshToken], isNull);
    });

    test('keeps the token when the network is the problem', () async {
      await storage.write(SecureStorageKeys.refreshToken, 'good-token');

      final failing = Dio(BaseOptions(baseUrl: 'http://unreachable.invalid/'))
        ..httpClientAdapter = _FailingAdapter();

      final api = ApiClient(
        baseUrl: 'http://unreachable.invalid/',
        storage: storage,
        tokens: tokens,
        dio: failing,
      );

      final offline = AuthRepository(
        api: api,
        storage: storage,
        tokens: tokens,
      );

      expect(await offline.restore(), isNull);

      // A flight-mode start is not a sign-out.
      expect(storage.values[SecureStorageKeys.refreshToken], 'good-token');
    });
  });

  group('signing out', () {
    test('clears the stored token even if the server is unreachable', () async {
      await storage.write(SecureStorageKeys.refreshToken, 'refresh-1');
      tokens.set(
        'access',
        DateTime.now().toUtc().add(const Duration(minutes: 5)),
      );

      final failing = Dio(BaseOptions(baseUrl: 'http://unreachable.invalid/'))
        ..httpClientAdapter = _FailingAdapter();

      final api = ApiClient(
        baseUrl: 'http://unreachable.invalid/',
        storage: storage,
        tokens: tokens,
        dio: failing,
      );

      await AuthRepository(
        api: api,
        storage: storage,
        tokens: tokens,
      ).signOut();

      expect(tokens.hasToken, isFalse);
      expect(storage.values[SecureStorageKeys.refreshToken], isNull);
    });
  });

  group('legal document types', () {
    test('travel by name, not by ordinal', () {
      expect(LegalDocumentType.privacy.wireName, 'Privacy');
      expect(LegalDocumentType.terms.wireName, 'Terms');
      expect(LegalDocumentType.scanNotice.wireName, 'ScanNotice');

      expect(LegalDocumentType.fromWire('Terms'), LegalDocumentType.terms);
    });

    test('an unknown name is refused rather than guessed', () {
      expect(
        () => LegalDocumentType.fromWire('Invented'),
        throwsA(isA<ArgumentError>()),
      );
    });
  });

  group('correlation id', () {
    test('every request carries one', () async {
      adapter.respond('/api/auth/login', 200, body: sessionBody());

      await repository.signIn(
        email: 'user@example.com',
        password: 'Password123',
      );

      // So a failure the user reports can be found on the server side
      // (docs/OBSERVABILITY.md).
      final sent = adapter.sentHeaders.single[correlationIdHeader] as String?;

      expect(sent, isNotNull);
      expect(RegExp(r'^[0-9a-f]{32}$').hasMatch(sent!), isTrue);
    });

    test('each request gets its own', () {
      final ids = {for (var i = 0; i < 50; i++) newCorrelationId()};

      // One per attempt: correlating a whole session into one id would defeat
      // the purpose.
      expect(ids.length, 50);
    });
  });
}

/// An adapter that always fails, standing in for no connection.
final class _FailingAdapter implements HttpClientAdapter {
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
