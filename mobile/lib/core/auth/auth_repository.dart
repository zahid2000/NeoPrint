import 'dart:math';

import '../network/api_client.dart';
import '../storage/secure_storage.dart';
import 'auth_models.dart';

/// Registration and sign-in against the backend (plan sections 2.8, 6).
final class AuthRepository {
  const AuthRepository({
    required ApiClient api,
    required SecureStorage storage,
    required AccessTokenHolder tokens,
  }) : _api = api,
       _storage = storage,
       _tokens = tokens;
  final ApiClient _api;
  final SecureStorage _storage;
  final AccessTokenHolder _tokens;

  /// Registers a user. No token is issued until the email is confirmed.
  Future<String> register({
    required String email,
    required String password,
    String? fullName,
    String language = 'az',
    String? referralCode,
    required List<PendingConsent> consents,
  }) async {
    final deviceId = await _deviceId();

    final json = await _api.send<Map<String, dynamic>>(
      method: 'POST',
      path: 'api/auth/register',
      authenticated: false,
      body: {
        'email': email.trim(),
        'password': password,
        'fullName': fullName?.trim(),
        'language': language,
        'referralCode': referralCode?.trim(),
        'deviceId': deviceId,
        'consents': consents
            .map(
              (consent) => {
                'docType': consent.docType.wireName,
                'version': consent.version,
              },
            )
            .toList(),
      },
      parse: (data) => data as Map<String, dynamic>,
    );

    return json['email'] as String;
  }

  /// Confirms the email address and signs the user in.
  Future<AuthSession> confirmEmail({
    required String email,
    required String code,
  }) async {
    final session = await _api.send<AuthSession>(
      method: 'POST',
      path: 'api/auth/confirm-email',
      authenticated: false,
      body: {
        'email': email.trim(),
        'code': code.trim(),
        'deviceId': await _deviceId(),
      },
      parse: (data) => AuthSession.fromJson(data as Map<String, dynamic>),
    );

    await _persist(session);
    return session;
  }

  /// Asks for a fresh confirmation code.
  Future<void> resendConfirmation(String email) => _api.send<void>(
    method: 'POST',
    path: 'api/auth/resend-confirmation',
    authenticated: false,
    body: {'email': email.trim()},
    parse: (_) {},
  );

  /// Signs the user in.
  Future<AuthSession> signIn({
    required String email,
    required String password,
  }) async {
    final session = await _api.send<AuthSession>(
      method: 'POST',
      path: 'api/auth/login',
      authenticated: false,
      body: {
        'email': email.trim(),
        'password': password,
        'deviceId': await _deviceId(),
      },
      parse: (data) => AuthSession.fromJson(data as Map<String, dynamic>),
    );

    await _persist(session);
    return session;
  }

  /// Restores a session from the stored refresh token.
  ///
  /// Returns null when there is nothing to restore, which is the normal state
  /// on a fresh install.
  Future<AuthSession?> restore() async {
    final refreshToken = await _storage.read(SecureStorageKeys.refreshToken);
    if (refreshToken == null || refreshToken.isEmpty) {
      return null;
    }

    try {
      final session = await _api.send<AuthSession>(
        method: 'POST',
        path: 'api/auth/refresh',
        authenticated: false,
        body: {'refreshToken': refreshToken, 'deviceId': await _deviceId()},
        parse: (data) => AuthSession.fromJson(data as Map<String, dynamic>),
      );

      await _persist(session);
      return session;
    } on ApiException catch (error) {
      // A refused token is spent or stolen; keeping it would only make every
      // start fail the same way.
      if (!error.isNetworkError) {
        await _storage.delete(SecureStorageKeys.refreshToken);
      }

      return null;
    }
  }

  /// Records the user's acceptance of the current document versions.
  Future<void> acceptConsents(List<PendingConsent> consents) async =>
      _api.send<void>(
        method: 'POST',
        path: 'api/consents',
        body: {
          'consents': consents
              .map(
                (consent) => {
                  'docType': consent.docType.wireName,
                  'version': consent.version,
                },
              )
              .toList(),
          'deviceId': await _deviceId(),
        },
        parse: (_) {},
      );

  /// The documents the signed-in user still has to accept.
  Future<List<PendingConsent>> pendingConsents({String language = 'az'}) =>
      _api.send<List<PendingConsent>>(
        method: 'GET',
        path: 'api/consents/pending',
        query: {'lang': language},
        parse: (data) => (data as List<dynamic>)
            .map(
              (item) => PendingConsent.fromJson(item as Map<String, dynamic>),
            )
            .toList(),
      );

  /// Fetches a legal document.
  Future<String> legalDocument(
    LegalDocumentType docType, {
    String language = 'az',
  }) => _api.send<String>(
    method: 'GET',
    path: 'api/legal/${docType.wireName}',
    authenticated: false,
    query: {'lang': language},
    parse: (data) => (data as Map<String, dynamic>)['content'] as String,
  );

  /// Signs out and forgets the stored token.
  Future<void> signOut() async {
    final refreshToken = await _storage.read(SecureStorageKeys.refreshToken);

    if (refreshToken != null && refreshToken.isNotEmpty) {
      try {
        await _api.send<void>(
          method: 'POST',
          path: 'api/auth/logout',
          authenticated: false,
          body: {'refreshToken': refreshToken},
          parse: (_) {},
        );
      } on ApiException {
        // Signing out locally must succeed even if the server cannot be
        // reached; the token is discarded either way.
      }
    }

    _tokens.clear();
    await _storage.delete(SecureStorageKeys.refreshToken);
  }

  Future<void> _persist(AuthSession session) async {
    _tokens.set(session.accessToken, session.accessTokenExpiresAt);

    // Plan section 2.8: the refresh token goes to the Keystore or the Keychain,
    // never anywhere else.
    await _storage.write(SecureStorageKeys.refreshToken, session.refreshToken);
  }

  /// A stable per-install identifier.
  ///
  /// Generated once and kept in secure storage. Used for the self-referral and
  /// repeat-referral check (plan section 2.6); it is not an advertising id and
  /// is not shared with anyone.
  Future<String> _deviceId() async {
    final existing = await _storage.read(SecureStorageKeys.deviceId);
    if (existing != null && existing.isNotEmpty) {
      return existing;
    }

    final random = Random.secure();
    final bytes = List<int>.generate(16, (_) => random.nextInt(256));
    final deviceId = bytes
        .map((byte) => byte.toRadixString(16).padLeft(2, '0'))
        .join();

    await _storage.write(SecureStorageKeys.deviceId, deviceId);
    return deviceId;
  }
}
