import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../l10n/app_language.dart';

import '../network/api_base_url.dart';
import '../network/api_client.dart';
import '../storage/secure_storage.dart';
import 'auth_models.dart';
import 'auth_repository.dart';
import '../push/push_service.dart';

/// The backend address for this build. See `api_base_url.dart`.
final apiBaseUrl = resolveApiBaseUrl();

final secureStorageProvider = Provider<SecureStorage>(
  (ref) => const PlatformSecureStorage(),
);

final accessTokenProvider = Provider<AccessTokenHolder>(
  (ref) => AccessTokenHolder(),
);

final apiClientProvider = Provider<ApiClient>((ref) {
  final client = ApiClient(
    baseUrl: apiBaseUrl,
    language: currentLanguageCode,
    storage: ref.watch(secureStorageProvider),
    tokens: ref.watch(accessTokenProvider),
  );

  // A refresh token the server refuses cannot be recovered from, so the app
  // returns to the sign-in screen rather than looping on 401s.
  client.onSessionExpired = () =>
      ref.read(authControllerProvider.notifier).onSessionExpired();

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

/// The signed-in user, for the screens that only show who is signed in.
///
/// Separate from the controller so a screen can be rendered with a user of its
/// own in a widget test without driving the sign-in flow.
final currentUserProvider = Provider<UserProfile?>(
  (ref) => ref.watch(authControllerProvider.select((state) => state.user)),
);

/// Drives registration, sign-in and consents (plan section 2.8).
///
/// The router listens to this, so every gate the plan requires is enforced in
/// one place: an unconfirmed email cannot reach the app, and neither can an
/// outstanding consent.
final class AuthController extends Notifier<AuthState> {
  /// How long the start screen waits before it asks the server again by itself.
  static const retryRestoreAfter = Duration(seconds: 5);

  Timer? _retry;

  @override
  AuthState build() {
    ref.onDispose(() => _retry?.cancel());

    // Restoring starts immediately; the splash screen shows while it runs.
    Future.microtask(restore);
    return const AuthState.restoring();
  }

  AuthRepository get _repository => ref.read(authRepositoryProvider);

  /// Restores a session from the stored refresh token.
  ///
  /// A server that cannot be reached is not a sign-out (T-269): the stored
  /// session stays, the start screen says the server is out of reach, and the
  /// restore is tried again - by itself every few seconds, and at once when
  /// the user asks.
  Future<void> restore() async {
    _retry?.cancel();

    if (state.status != AuthStatus.restoring) {
      return;
    }

    try {
      final session = await _repository.restore();

      state = session == null
          ? const AuthState.signedOut()
          : _fromSession(session);
    } on ApiException {
      state = const AuthState.restoring(stalled: true);
      _retry = Timer(retryRestoreAfter, () => unawaited(restore()));
    }
  }

  /// Registers a user and moves to the confirmation screen.
  Future<void> register({
    required String email,
    required String password,
    String? fullName,
    String? language,
    String? referralCode,
    required List<PendingConsent> consents,
  }) async {
    state = state.copyWith(isBusy: true, clearError: true);

    try {
      final registeredEmail = await _repository.register(
        email: email,
        password: password,
        fullName: fullName,
        language: language ?? currentLanguageCode(),
        referralCode: referralCode,
        consents: consents,
      );

      state = AuthState(
        status: AuthStatus.awaitingEmailConfirmation,
        pendingEmail: registeredEmail,
      );
    } on ApiException catch (error) {
      state = state.copyWith(isBusy: false, error: error);
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
      state = _fromSession(
        await _repository.confirmEmail(email: email, code: code),
      );
    } on ApiException catch (error) {
      state = state.copyWith(isBusy: false, error: error);
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
      state = state.copyWith(error: error);
    }
  }

  /// Signs the user in.
  Future<void> signIn({required String email, required String password}) async {
    state = state.copyWith(isBusy: true, clearError: true);

    try {
      state = _fromSession(
        await _repository.signIn(email: email, password: password),
      );
    } on ApiException catch (error) {
      state = AuthState.signedOut(error: error);
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
      state = state.copyWith(isBusy: false, error: error);
    }
  }

  /// The account's language was saved (`PUT users/me`, T-152): the profile
  /// held here says so too.
  void languageSaved(String language) {
    final user = state.user;
    if (user == null || user.language == language) {
      return;
    }

    state = AuthState(
      status: state.status,
      user: UserProfile(
        id: user.id,
        publicId: user.publicId,
        email: user.email,
        fullName: user.fullName,
        language: language,
        emailConfirmed: user.emailConfirmed,
        pendingConsents: user.pendingConsents,
      ),
    );
  }

  /// Signs out, and this device stops getting the user's push (T-268): its
  /// push token goes to the server with the sign-out itself, and the phone
  /// gives the token up, so nothing sent to it arrives any more - whether or
  /// not the server heard. A failure there does not keep anyone in.
  Future<void> signOut() async {
    _retry?.cancel();
    _signingOut = true;
    try {
      String? pushToken;
      try {
        pushToken = await ref.read(pushRegistrationProvider).release();
      } on Exception {
        // The phone's push service would not say (a platform exception, a
        // timeout): the sign-out goes on without it. Only exceptions - an
        // Error here is a fault in the app, and swallowing one is how this
        // step went unnoticed doing nothing at all (T-268).
      }
      await _repository.signOut(pushToken: pushToken);
      state = const AuthState.signedOut();
    } finally {
      _signingOut = false;
    }
  }

  /// Called by the API client when the refresh token is refused.
  ///
  /// Not after the user signed out: a request still on its way then (the
  /// hub, a list refreshing) is refused too, and "session expired" would
  /// contradict "signed out" - or "your account was deleted".
  void onSessionExpired() {
    if (state.status == AuthStatus.signedOut || _signingOut) {
      return;
    }

    // Nobody is signed in on this phone any more: it stops getting the
    // account's push too. The server cannot be told (there is no session to
    // tell it with), so the phone gives its token up (T-268).
    unawaited(
      ref
          .read(pushRegistrationProvider)
          .release()
          .then<void>((_) {})
          .catchError((Object _) {}, test: (error) => error is Exception),
    );

    state = const AuthState.signedOut(error: ApiException.sessionExpired());
  }

  bool _signingOut = false;

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
