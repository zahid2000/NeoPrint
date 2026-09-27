import '../network/api_client.dart';

/// The documents a user consents to (plan sections 2.8, 2.13).
enum LegalDocumentType {
  privacy('Privacy'),
  terms('Terms'),
  scanNotice('ScanNotice');

  const LegalDocumentType(this.wireName);

  /// The name the API uses. Enums travel by name, not by ordinal, so
  /// reordering this list cannot change what a stored value means.
  final String wireName;

  static LegalDocumentType fromWire(String value) => values.firstWhere(
    (type) => type.wireName == value,
    orElse: () => throw ArgumentError('Unknown legal document type: $value'),
  );
}

/// A document the user still has to accept.
final class PendingConsent {
  const PendingConsent({required this.docType, required this.version});

  final LegalDocumentType docType;
  final String version;

  factory PendingConsent.fromJson(Map<String, dynamic> json) => PendingConsent(
    docType: LegalDocumentType.fromWire(json['docType'] as String),
    version: json['version'] as String,
  );
}

/// The signed-in user's profile.
final class UserProfile {
  const UserProfile({
    required this.id,
    required this.publicId,
    required this.email,
    required this.language,
    required this.emailConfirmed,
    this.fullName,
    this.pendingConsents = const [],
  });

  final String id;

  /// The shareable code, `NP-XXXXXX` (plan section 5).
  final String publicId;

  final String email;
  final String? fullName;
  final String language;
  final bool emailConfirmed;

  /// Documents whose current version has not been accepted. A new version
  /// being published puts entries here at the next sign-in (plan section 2.8).
  final List<PendingConsent> pendingConsents;

  bool get hasPendingConsents => pendingConsents.isNotEmpty;

  factory UserProfile.fromJson(Map<String, dynamic> json) => UserProfile(
    id: json['id'] as String,
    publicId: json['publicId'] as String,
    email: json['email'] as String,
    fullName: json['fullName'] as String?,
    language: (json['language'] as String?) ?? 'az',
    emailConfirmed: (json['emailConfirmed'] as bool?) ?? false,
    pendingConsents: ((json['pendingConsents'] as List<dynamic>?) ?? const [])
        .map((item) => PendingConsent.fromJson(item as Map<String, dynamic>))
        .toList(),
  );
}

/// A signed-in session.
final class AuthSession {
  const AuthSession({
    required this.accessToken,
    required this.accessTokenExpiresAt,
    required this.refreshToken,
    required this.user,
  });

  final String accessToken;
  final DateTime accessTokenExpiresAt;
  final String refreshToken;
  final UserProfile user;

  factory AuthSession.fromJson(Map<String, dynamic> json) => AuthSession(
    accessToken: json['accessToken'] as String,
    accessTokenExpiresAt: DateTime.parse(json['accessTokenExpiresAt'] as String)
        .toUtc(),
    refreshToken: json['refreshToken'] as String,
    user: UserProfile.fromJson(json['user'] as Map<String, dynamic>),
  );
}

/// Where the user is in the sign-in flow.
///
/// Drives the router, so every gate the plan requires is one state rather than
/// a scattering of boolean checks: an unconfirmed email cannot reach the app,
/// and neither can an outstanding consent (plan section 2.8).
enum AuthStatus {
  /// Reading the stored refresh token.
  restoring,

  /// Nobody is signed in.
  signedOut,

  /// Registered, waiting for the email confirmation code.
  awaitingEmailConfirmation,

  /// Signed in, but a document version has not been accepted.
  awaitingConsents,

  /// Signed in and ready.
  signedIn,
}

/// What the sign-in flow currently holds.
final class AuthState {
  const AuthState({
    required this.status,
    this.user,
    this.pendingEmail,
    this.error,
    this.isBusy = false,
  });

  const AuthState.restoring() : this(status: AuthStatus.restoring);

  const AuthState.signedOut({ApiException? error})
    : this(status: AuthStatus.signedOut, error: error);

  final AuthStatus status;
  final UserProfile? user;

  /// The address awaiting confirmation, so the confirmation screen can show it.
  final String? pendingEmail;

  /// Why the last action failed; the screen words it (`apiErrorText`).
  final ApiException? error;
  final bool isBusy;

  AuthState copyWith({
    AuthStatus? status,
    UserProfile? user,
    String? pendingEmail,
    ApiException? error,
    bool? isBusy,
    bool clearError = false,
    bool clearUser = false,
  }) => AuthState(
    status: status ?? this.status,
    user: clearUser ? null : (user ?? this.user),
    pendingEmail: pendingEmail ?? this.pendingEmail,
    error: clearError ? null : (error ?? this.error),
    isBusy: isBusy ?? this.isBusy,
  );
}
