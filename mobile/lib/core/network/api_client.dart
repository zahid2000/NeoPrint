import 'dart:async';
import 'dart:convert';

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
    this.code,
    this.feature,
    this.noInternet = false,
  });

  const ApiException.unreachable()
    : this(statusCode: null, message: '', kind: ApiErrorKind.unreachable);

  /// Unreachable because this device has no network (T-199).
  const ApiException.offline()
    : this(
        statusCode: null,
        message: '',
        kind: ApiErrorKind.unreachable,
        noInternet: true,
      );

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

  /// The server's message key (`code` in the problem document), for reacting
  /// to a specific rule without reading the sentence.
  final String? code;

  /// The feature flag that refused the request (`features.unavailable`).
  final String? feature;

  /// A feature switched off on the server refused it (plan section 2.16).
  bool get isFeatureOff => code == featureOffCode;

  static const featureOffCode = 'features.unavailable';

  /// Whether this was a network problem rather than a refusal.
  bool get isNetworkError => kind == ApiErrorKind.unreachable;

  /// Unreachable, and the request never left the device: no network, or the
  /// server's name could not be looked up (T-199). False when the server
  /// itself did not answer.
  final bool noInternet;

  /// The first error recorded against a field, if any.
  String? errorFor(String field) => fieldErrors[field]?.firstOrNull;

  @override
  String toString() => 'ApiException($statusCode): $message';
}

/// How an attempt to renew the session ended (T-269).
enum SessionRenewal {
  /// A new access token, and the refresh token that replaces the one sent.
  renewed,

  /// There is no stored refresh token: nobody was signed in on this device.
  none,

  /// The server refused the refresh token: it is spent, revoked or expired.
  /// The session is over and the user signs in again.
  ended,

  /// No verdict on the token: the server could not be reached, was too busy
  /// or failed. The session is still the user's - the stored token is kept
  /// and the renewal is tried again later.
  unavailable,
}

/// What a renewal gave back: the session as the server sent it, when it did.
final class RenewedSession {
  const RenewedSession(this.outcome, [this.json, this.noInternet = false]);

  final SessionRenewal outcome;

  /// [SessionRenewal.unavailable] because this device has no network (T-199).
  final bool noInternet;

  /// The body of `POST auth/refresh` when [outcome] is [SessionRenewal.renewed].
  final Map<String, dynamic>? json;
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
  Future<RenewedSession>? _refreshing;

  /// Called when the session cannot be recovered and the user must sign in.
  void Function()? onSessionExpired;

  /// Called when the server says a feature is switched off, so the app can
  /// read its flags again and hide the way in.
  void Function()? onFeatureOff;

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
      switch ((await renewSession()).outcome) {
        case SessionRenewal.renewed:
          try {
            reply = await attempt();
          } on DioException catch (error) {
            throw _toApiException(error);
          }

        case SessionRenewal.unavailable:
          // Nothing was said about the session: it is kept, and this request
          // failed the way any request fails when the server is out of reach.
          throw const ApiException.unreachable();

        case SessionRenewal.none:
        case SessionRenewal.ended:
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
      final problem = _fromProblemDetails(status, reply.data);

      if (problem.isFeatureOff) {
        onFeatureOff?.call();
      }

      throw problem;
    }

    response?.call(reply);

    return parse(reply.data);
  }

  /// The access token for a connection that cannot refresh on a 401 by
  /// itself - the user hub's WebSocket (plan section 6). Refreshed first when
  /// it is about to run out; null when there is no session.
  Future<String?> freshAccessToken() async {
    if (!_tokens.hasToken || _tokens.isNearExpiry) {
      if ((await renewSession()).outcome != SessionRenewal.renewed) {
        return null;
      }
    }

    return _tokens.token;
  }

  /// Exchanges the stored refresh token for a new session - the one place
  /// that does. At most one runs at a time and every caller gets its answer:
  /// the token rotates on use, and a second exchange of the same token is a
  /// replay, which the server answers by ending every session of the user
  /// (plan section 2.8). Start-up restores the session through here too.
  ///
  /// [deviceId] goes with the exchange when this call starts it.
  Future<RenewedSession> renewSession({String? deviceId}) {
    return _refreshing ??= _performRefresh(deviceId).whenComplete(() {
      _refreshing = null;
    });
  }

  Future<RenewedSession> _performRefresh(String? deviceId) async {
    final refreshToken = await _storage.read(SecureStorageKeys.refreshToken);
    if (refreshToken == null || refreshToken.isEmpty) {
      return const RenewedSession(SessionRenewal.none);
    }

    try {
      final response = await _dio.post<dynamic>(
        'api/auth/refresh',
        data: {'refreshToken': refreshToken, 'deviceId': ?deviceId},
        options: Options(extra: {'skipAuth': true}),
      );

      final status = response.statusCode ?? 0;
      if (status < 200 || status >= 300) {
        // Only a refusal of the token itself ends the session. "Too many
        // requests" or a server that is starting up says nothing about it:
        // throwing the token away then signed people out for no reason
        // (T-269).
        return RenewedSession(
          status == 400 || status == 401 || status == 403
              ? SessionRenewal.ended
              : SessionRenewal.unavailable,
        );
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

      return RenewedSession(SessionRenewal.renewed, json);
    } on DioException catch (error) {
      return RenewedSession(
        SessionRenewal.unavailable,
        null,
        isNoInternet(error),
      );
    }
  }

  /// Whether a failed request never left this device: no network, or a name
  /// that cannot be looked up (T-199). Read from the platform's own message -
  /// the app has no connectivity package (plan section 2.0). A server that
  /// refuses the connection or does not answer in time is not this.
  static bool isNoInternet(DioException error) =>
      error.response == null &&
      (error.type == DioExceptionType.connectionError ||
          error.type == DioExceptionType.unknown) &&
      _noInternet.hasMatch('${error.error ?? ''} ${error.message ?? ''}');

  static final _noInternet = RegExp(
    r'Failed host lookup|Network is unreachable|No address associated with '
    r'hostname|nodename nor servname|errno = (7|51|101)\b',
    caseSensitive: false,
  );

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

    return isNoInternet(error)
        ? const ApiException.offline()
        : const ApiException.unreachable();
  }

  static Object? _decodeJson(List<int> bytes) {
    try {
      return jsonDecode(utf8.decode(bytes));
    } on FormatException {
      return null;
    }
  }

  /// Reads an RFC 7807 problem document (plan section 6).
  static ApiException _fromProblemDetails(int status, dynamic body) {
    // A request for bytes - a page image - gets its refusal as bytes too. It
    // is still a problem document, and its sentence is what the screen shows.
    final data = body is List<int> ? _decodeJson(body) : body;

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
      code: data['code'] as String?,
      feature: data['feature'] as String?,
    );
  }
}
