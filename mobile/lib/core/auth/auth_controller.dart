import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../network/api_client.dart';
import '../storage/secure_storage.dart';
import 'auth_models.dart';
import 'auth_repository.dart';

/// The backend address for this build.
///
/// Overridden at build time with `--dart-define=NEOPRINT_API_URL=...`, which is
/// how the staging and production builds differ. It is not a secret.
const apiBaseUrl = String.fromEnvironment(
  'NEOPRINT_API_URL',
  defaultValue: 'https://api.neoprint.az',
);

final secureStorageProvider = Provider<SecureStorage>(
  (ref) => const PlatformSecureStorage(),
);

final accessTokenProvider = Provider<AccessTokenHolder>(
  (ref) => AccessTokenHolder(),
);

final apiClientProvider = Provider<ApiClient>((ref) {
  final client = ApiClient(
    baseUrl: apiBaseUrl,
    storage: ref.watch(secureStorageProvider),
    tokens: ref.watch(accessTokenProvider),
  );

  // A refresh token the server refuses cannot be recovered from, so the app
  // returns to the sign-in screen rather than looping on 401s.
  client.onSessionExpired = () => ref.read(authControllerProvider.notifier).onSessionExpired();

  return client;
});

final authRepositoryProvider = Provider<AuthRepository>(
  (ref) => AuthRepository(
    api: ref.watch(apiClientProvider),
    storage: ref.watch(secureStorageProvider),
    tokens: ref.watch(accessTokenProvider),
  ),
);

final authControllerProvider = NotifierProvider<AuthController, AuthState>(
  AuthController.new,
);

/// Drives registration, sign-in and consents (plan section 2.8).
///
/// The router listens to this, so every gate the plan requires is enforced in
/// one place: an unconfirmed email cannot reach the app, and neither can an
/// outstanding consent.
final class AuthController extends Notifier<AuthState> {
  @override
  AuthState build() {
    // Restoring starts immediately; the splash screen shows while it runs.
    Future.microtask(restore);
    return const AuthState.restoring();
  }

  AuthRepository get _repository => ref.read(authRepositoryProvider);

  /// Restores a session from the stored refresh token.
  Future<void> restore() async {
    try {
      final session = await _repository.restore();

      if (session == null) {
        state = const AuthState.signedOut();
        return;
      }

      state = _fromSession(session);
    } on ApiException catch (error) {
      // A network failure at start is not a sign-out: the user keeps their
      // stored token and can retry.
      state = AuthState.signedOut(
        errorMessage: error.isNetworkError ? error.message : null,
      );
    }
  }

  /// Registers a user and moves to the confirmation screen.
  Future<void> register({
    required String email,
    required String password,
    String? fullName,
    String language = 'az',
    String? referralCode,
    required List<PendingConsent> consents,
  }) async {
    state = state.copyWith(isBusy: true, clearError: true);

    try {
      final registeredEmail = await _repository.register(
        email: email,
        password: password,
        fullName: fullName,
        language: language,
        referralCode: referralCode,
        consents: consents,
      );

      state = AuthState(
        status: AuthStatus.awaitingEmailConfirmation,
        pendingEmail: registeredEmail,
      );
    } on ApiException catch (error) {
      state = state.copyWith(isBusy: false, errorMessage: error.message);
    }
  }

  /// Confirms the email address and signs in.
  Future<void> confirmEmail(String code) async {
    final email = state.pendingEmail;
    if (email == null) {
      return;
    }

    state = state.copyWith(isBusy: true, clearError: true);

    try {
      state = _fromSession(await _repository.confirmEmail(email: email, code: code));
    } on ApiException catch (error) {
      state = state.copyWith(isBusy: false, errorMessage: error.message);
    }
  }

  /// Asks for a fresh confirmation code.
  Future<void> resendConfirmation() async {
    final email = state.pendingEmail;
    if (email == null) {
      return;
    }

    try {
      await _repository.resendConfirmation(email);
    } on ApiException catch (error) {
      state = state.copyWith(errorMessage: error.message);
    }
  }

  /// Signs the user in.
  Future<void> signIn({required String email, required String password}) async {
    state = state.copyWith(isBusy: true, clearError: true);

    try {
      state = _fromSession(await _repository.signIn(email: email, password: password));
    } on ApiException catch (error) {
      state = AuthState.signedOut(errorMessage: error.message);
    }
  }

  /// Records acceptance of the current document versions.
  Future<void> acceptConsents(List<PendingConsent> consents) async {
    state = state.copyWith(isBusy: true, clearError: true);

    try {
      await _repository.acceptConsents(consents);

      // The pending list is cleared locally rather than refetched: the server
      // has just recorded exactly what was sent.
      final user = state.user;

      state = AuthState(
        status: AuthStatus.signedIn,
        user: user == null
            ? null
            : UserProfile(
                id: user.id,
                publicId: user.publicId,
                email: user.email,
                fullName: user.fullName,
                language: user.language,
                emailConfirmed: user.emailConfirmed,
              ),
      );
    } on ApiException catch (error) {
      state = state.copyWith(isBusy: false, errorMessage: error.message);
    }
  }

  /// Signs out.
  Future<void> signOut() async {
    await _repository.signOut();
    state = const AuthState.signedOut();
  }

  /// Called by the API client when the refresh token is refused.
  void onSessionExpired() {
    state = const AuthState.signedOut(
      errorMessage: 'Sessiya bitib. Yenidən daxil olun.',
    );
  }

  /// Clears the error banner.
  void clearError() => state = state.copyWith(clearError: true);

  static AuthState _fromSession(AuthSession session) => AuthState(
    // Plan section 2.8: a document version that has not been accepted stops
    // here, before the rest of the app.
    status: session.user.hasPendingConsents
        ? AuthStatus.awaitingConsents
        : AuthStatus.signedIn,
    user: session.user,
  );
}
