import 'dart:ui';

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

/// The language the app is showing right now.
String currentLanguageCode() =>
    resolveLanguageCode(PlatformDispatcher.instance.locale);
