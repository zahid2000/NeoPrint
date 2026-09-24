import 'dart:async';

import 'package:dio/dio.dart';

import '../storage/secure_storage.dart';
import 'correlation_id.dart';

/// What the API returned when a request failed.
///
/// The backend answers with RFC 7807 problem details (plan section 6), so the
/// message shown to the user comes from the server rather than being invented
/// here - the server already writes it in the right language.
final class ApiException implements Exception {
  const ApiException({
    required this.statusCode,
    required this.message,
    this.fieldErrors = const {},
  });

  final int? statusCode;
  final String message;

  /// Validation errors grouped by field, for marking up the form.
  final Map<String, List<String>> fieldErrors;

  /// Whether this was a network problem rather than a refusal.
  bool get isNetworkError => statusCode == null;

  /// The first error recorded against a field, if any.
  String? errorFor(String field) => fieldErrors[field]?.firstOrNull;

  @override
  String toString() => 'ApiException($statusCode): $message';
}

/// Holds the access token for the life of the process.
///
/// Deliberately in memory: the access token lasts fifteen minutes, and putting
/// it in the keychain would add a round trip to every request for no security
/// gain. The refresh token is the one that is stored (plan section 2.8).
final class AccessTokenHolder {
  String? _token;
  DateTime? _expiresAt;

  String? get token => _token;

  bool get hasToken => _token != null;

  /// Whether the token is close enough to expiry to be worth refreshing.
  bool get isNearExpiry {
    final expiresAt = _expiresAt;
    if (expiresAt == null) return true;

    return DateTime.now().toUtc().isAfter(
      expiresAt.subtract(const Duration(minutes: 1)),
    );
  }

  void set(String token, DateTime expiresAt) {
    _token = token;
    _expiresAt = expiresAt;
  }

  void clear() {
    _token = null;
    _expiresAt = null;
  }
}

/// The application's HTTP client.
///
/// Attaches the access token, and on a 401 refreshes once and replays the
/// request. Concurrent requests share a single refresh: five screens loading at
/// once must not send five refreshes, because the token rotates and four of
/// them would be rejected as replays (plan section 2.8).
final class ApiClient {
  ApiClient({
    required String baseUrl,
    required SecureStorage storage,
    required AccessTokenHolder tokens,
    Dio? dio,
  }) : _storage = storage,
       _tokens = tokens,
       _dio =
           dio ??
           Dio(
             BaseOptions(
               baseUrl: baseUrl,
               connectTimeout: const Duration(seconds: 15),
               receiveTimeout: const Duration(seconds: 30),
               contentType: 'application/json',
               // Handled below, so a 4xx does not become an exception before
               // the refresh has had a chance to run.
               validateStatus: (_) => true,
             ),
           ) {
    _dio.interceptors.add(
      InterceptorsWrapper(onRequest: _onRequest, onResponse: _onResponse),
    );
  }

  final Dio _dio;
  final SecureStorage _storage;
  final AccessTokenHolder _tokens;

  /// The refresh in flight, so concurrent callers await the same one.
  Future<bool>? _refreshing;

  /// Called when the session cannot be recovered and the user must sign in.
  void Function()? onSessionExpired;

  Dio get dio => _dio;

  Future<void> _onRequest(
    RequestOptions options,
    RequestInterceptorHandler handler,
  ) async {
    if (_tokens.hasToken && options.extra['skipAuth'] != true) {
      options.headers['Authorization'] = 'Bearer ${_tokens.token}';
    }

    // A correlation id on every request, so a failure the user reports can be
    // found on the server side (docs/OBSERVABILITY.md). One per attempt, and
    // never overwritten: a caller that set one is tying this request to work
    // that started earlier.
    options.headers.putIfAbsent(correlationIdHeader, newCorrelationId);

    handler.next(options);
  }

  Future<void> _onResponse(
    Response<dynamic> response,
    ResponseInterceptorHandler handler,
  ) async {
    handler.next(response);
  }

  /// Sends a request, refreshing the token once if the server says the token
  /// has expired.
  Future<T> send<T>({
    required String method,
    required String path,
    Object? body,
    Map<String, dynamic>? query,
    bool authenticated = true,
    required T Function(dynamic json) parse,
  }) async {
    Future<Response<dynamic>> attempt() => _dio.request<dynamic>(
      path,
      data: body,
      queryParameters: query,
      options: Options(
        method: method,
        extra: {'skipAuth': !authenticated},
      ),
    );

    Response<dynamic> response;

    try {
      response = await attempt();
    } on DioException catch (error) {
      throw _toApiException(error);
    }

    if (response.statusCode == 401 && authenticated) {
      if (await _refreshOnce()) {
        try {
          response = await attempt();
        } on DioException catch (error) {
          throw _toApiException(error);
        }
      } else {
        _tokens.clear();
        await _storage.delete(SecureStorageKeys.refreshToken);
        onSessionExpired?.call();

        throw const ApiException(
          statusCode: 401,
          message: 'Sessiya bitib. Yenidən daxil olun.',
        );
      }
    }

    final status = response.statusCode ?? 0;

    if (status < 200 || status >= 300) {
      throw _fromProblemDetails(status, response.data);
    }

    return parse(response.data);
  }

  /// Runs at most one refresh at a time and hands the result to every waiter.
  Future<bool> _refreshOnce() {
    return _refreshing ??= _performRefresh().whenComplete(() {
      _refreshing = null;
    });
  }

  Future<bool> _performRefresh() async {
    final refreshToken = await _storage.read(SecureStorageKeys.refreshToken);
    if (refreshToken == null || refreshToken.isEmpty) {
      return false;
    }

    try {
      final response = await _dio.post<dynamic>(
        'api/auth/refresh',
        data: {'refreshToken': refreshToken},
        options: Options(extra: {'skipAuth': true}),
      );

      final status = response.statusCode ?? 0;
      if (status < 200 || status >= 300) {
        return false;
      }

      final json = response.data as Map<String, dynamic>;

      _tokens.set(
        json['accessToken'] as String,
        DateTime.parse(json['accessTokenExpiresAt'] as String).toUtc(),
      );

      // The refresh token rotates on every use, so the new one has to replace
      // the old one or the next refresh looks like a replay.
      await _storage.write(
        SecureStorageKeys.refreshToken,
        json['refreshToken'] as String,
      );

      return true;
    } on DioException {
      return false;
    }
  }

  static ApiException _toApiException(DioException error) {
    if (error.response != null) {
      return _fromProblemDetails(
        error.response!.statusCode ?? 0,
        error.response!.data,
      );
    }

    return const ApiException(
      statusCode: null,
      message: 'Şəbəkə xətası. Bağlantınızı yoxlayın.',
    );
  }

  /// Reads an RFC 7807 problem document (plan section 6).
  static ApiException _fromProblemDetails(int status, dynamic data) {
    if (data is! Map<String, dynamic>) {
      return ApiException(
        statusCode: status,
        message: 'Serverdə xəta baş verdi ($status).',
      );
    }

    final fieldErrors = <String, List<String>>{};
    final errors = data['errors'];

    if (errors is Map<String, dynamic>) {
      for (final entry in errors.entries) {
        final value = entry.value;
        if (value is List) {
          fieldErrors[entry.key] = value.map((e) => e.toString()).toList();
        }
      }
    }

    // Prefer the specific detail, then the title, and only then a generic
    // message: the server's own wording is the most useful thing to show.
    final message =
        (data['detail'] as String?) ??
        (fieldErrors.values.firstOrNull?.firstOrNull) ??
        (data['title'] as String?) ??
        'Serverdə xəta baş verdi ($status).';

    return ApiException(
      statusCode: status,
      message: message,
      fieldErrors: fieldErrors,
    );
  }
}
