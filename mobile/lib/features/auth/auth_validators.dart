import '../../l10n/app_localizations.dart';

/// Form validation for the auth screens.
///
/// These mirror the server's rules (plan section 2.8) so the user is told
/// before a round trip. The server validates independently - this is
/// convenience, not protection.
abstract final class AuthValidators {
  /// Matches the server's minimum (plan section 2.8).
  static const minimumPasswordLength = 8;

  /// Confirmation codes are six digits.
  static const confirmationCodeLength = 6;

  /// Checks an email address.
  ///
  /// A single @ with something either side and a dot in the domain is as much
  /// as is worth checking here: the confirmation code proves the rest, and a
  /// stricter pattern would reject valid addresses.
  static String? email(String? value, AppLocalizations l10n) {
    final trimmed = value?.trim() ?? '';

    if (trimmed.isEmpty) {
      return l10n.authEmailRequired;
    }

    final at = trimmed.indexOf('@');

    if (at <= 0 ||
        at != trimmed.lastIndexOf('@') ||
        at == trimmed.length - 1 ||
        !trimmed.substring(at + 1).contains('.') ||
        trimmed.length > 254) {
      return l10n.authEmailInvalid;
    }

    return null;
  }

  /// Checks a password being chosen.
  static String? password(String? value, AppLocalizations l10n) {
    if (value == null || value.isEmpty) {
      return l10n.authPasswordRequired;
    }

    if (value.length < minimumPasswordLength) {
      return l10n.authPasswordTooShort(minimumPasswordLength);
    }

    return null;
  }

  /// Checks only that a password was entered.
  ///
  /// Used at sign-in: applying the length rule there would lock out anyone
  /// whose password predates it.
  static String? passwordPresent(String? value, AppLocalizations l10n) {
    if (value == null || value.isEmpty) {
      return l10n.authPasswordRequired;
    }

    return null;
  }

  /// Checks a six-digit confirmation code.
  static String? confirmationCode(String? value, AppLocalizations l10n) {
    final trimmed = value?.trim() ?? '';

    if (trimmed.length != confirmationCodeLength ||
        !RegExp(r'^\d{6}$').hasMatch(trimmed)) {
      return l10n.confirmEmailCodeInvalid;
    }

    return null;
  }
}
