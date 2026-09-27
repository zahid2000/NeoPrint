import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import '../storage/secure_storage.dart';
import 'api_base_url.dart';
import 'correlation_id.dart';

/// Why a request failed, as far as the screen needs to know.
enum ApiErrorKind {
  /// No answer at all: offline, the server down, or a request that could not
  /// be sent. The screen says the server could not be reached.
  unreachable,

  /// The user or the app cancelled the request.
  cancelled,

  /// The session could not be refreshed; the user has to sign in again.
  sessionExpired,

  /// The server answered and refused. [ApiException.message] is its own
  /// sentence when it sent one.
  server,
}

/// What the API returned when a request failed.
///
/// The backend answers with RFC 7807 problem details (plan section 6), so a
/// refusal is shown in the server's own words - it writes them in the language
/// the request asked for (`Accept-Language`). Failures that never reached the
/// server have no server text; the screen words those from the app's own
/// translations, by [kind] (`apiErrorText`).
final class ApiException implements Exception {
  const ApiException({
    required this.statusCode,
    required this.message,
    this.kind = ApiErrorKind.server,
    this.fieldErrors = const {},
  });

  const ApiException.unreachable()
    : this(statusCode: null, message: '', kind: ApiErrorKind.unreachable);

  const ApiException.cancelled()
    : this(statusCode: 0, message: '', kind: ApiErrorKind.cancelled);

  const ApiException.sessionExpired()
    : this(statusCode: 401, message: '', kind: ApiErrorKind.sessionExpired);

  final int? statusCode;

  /// The server's sentence; empty when it sent none.
  final String message;

  final ApiErrorKind kind;

  /// Validation errors grouped by field, for marking up the form.
  final Map<String, List<String>> fieldErrors;

  /// Whether this was a network problem rather than a refusal.
  bool get isNetworkError => kind == ApiErrorKind.unreachable;

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
    String Function()? language,
    Dio? dio,
  }) : _storage = storage,
       _tokens = tokens,
       _language = language,
       _dio =
           dio ??
           Dio(
             BaseOptions(
               baseUrl: withTrailingSlash(baseUrl),
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

  /// The app's language, sent as `Accept-Language` so the server words its
  /// errors and emails the way the user reads the screen.
  final String Function()? _language;

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

    final language = _language?.call();
    if (language != null) {
      options.headers.putIfAbsent('Accept-Language', () => language);
    }

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
  ///
  /// [responseType] set to [ResponseType.bytes] hands [parse] the raw bytes -
  /// page images. [onSendProgress] reports an upload as it goes, and
  /// [cancelToken] stops it. [headers] are sent as well as the usual ones.
  /// A [response] callback sees the whole response, for its headers.
  Future<T> send<T>({
    required String method,
    required String path,
    Object? body,
    Map<String, dynamic>? query,
    Map<String, dynamic>? headers,
    bool authenticated = true,
    ResponseType? responseType,
    ProgressCallback? onSendProgress,
    CancelToken? cancelToken,
    void Function(Response<dynamic> response)? response,
    required T Function(dynamic json) parse,
  }) async {
    Future<Response<dynamic>> attempt() => _dio.request<dynamic>(
      path,
      // A multipart body is a stream and can be read once; the retry after a
      // refresh needs a fresh copy.
      data: body is FormData ? body.clone() : body,
      queryParameters: query,
      onSendProgress: onSendProgress,
      cancelToken: cancelToken,
      options: Options(
        method: method,
        headers: headers,
        responseType: responseType,
        extra: {'skipAuth': !authenticated},
      ),
    );

    Response<dynamic> reply;

    try {
      reply = await attempt();
    } on DioException catch (error) {
      throw _toApiException(error);
    }

    if (reply.statusCode == 401 && authenticated) {
      if (await _refreshOnce()) {
        try {
          reply = await attempt();
        } on DioException catch (error) {
          throw _toApiException(error);
        }
      } else {
        _tokens.clear();
        await _storage.delete(SecureStorageKeys.refreshToken);
        onSessionExpired?.call();

        throw const ApiException.sessionExpired();
      }
    }

    final status = reply.statusCode ?? 0;

    if (status == 304) {
      response?.call(reply);
      return parse(null);
    }

    if (status < 200 || status >= 300) {
      throw _fromProblemDetails(status, reply.data);
    }

    response?.call(reply);

    return parse(reply.data);
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
    if (error.type == DioExceptionType.cancel) {
      return const ApiException.cancelled();
    }

    if (error.response == null && kDebugMode) {
      // What actually went wrong, for whoever is debugging: the user only
      // ever sees "could not reach the server". No URL query, header or body
      // is printed, so nothing sensitive reaches the console.
      debugPrint(
        'Request to ${error.requestOptions.path} failed without a response: '
        '${error.type.name} ${error.error ?? error.message}',
      );
    }

    if (error.response != null) {
      return _fromProblemDetails(
        error.response!.statusCode ?? 0,
        error.response!.data,
      );
    }

    return const ApiException.unreachable();
  }

  /// Reads an RFC 7807 problem document (plan section 6).
  static ApiException _fromProblemDetails(int status, dynamic data) {
    if (data is! Map<String, dynamic>) {
      return ApiException(statusCode: status, message: '');
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
        '';

    return ApiException(
      statusCode: status,
      message: message,
      fieldErrors: fieldErrors,
    );
  }
}
