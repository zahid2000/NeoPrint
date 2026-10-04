import 'package:flutter/widgets.dart';

/// The languages the app is written in (plan section 2.1). The first is the
/// default.
const supportedLanguageCodes = ['az', 'en', 'ru'];

/// The app's language for a device locale: the device's own when it is one of
/// ours, Azerbaijani otherwise.
///
/// One rule for the screen and for the server, so a user reading English also
/// gets the server's messages in English.
String resolveLanguageCode(Locale? device) {
  final code = device?.languageCode;
  return supportedLanguageCodes.contains(code)
      ? code!
      : supportedLanguageCodes.first;
}

/// The phone's language, as the app would show it. Through the binding, so a
/// test can set the phone's locale.
String phoneLanguageCode() =>
    resolveLanguageCode(WidgetsBinding.instance.platformDispatcher.locale);

/// The language chosen in the profile (T-152), or null to follow the phone.
/// Set by `AppLanguageController`; read where no provider is at hand - the
/// API client's `Accept-Language`.
String? chosenLanguageCode;

/// The language the app is showing right now.
String currentLanguageCode() => chosenLanguageCode ?? phoneLanguageCode();
