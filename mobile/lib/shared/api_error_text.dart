import '../core/features/feature_flags.dart';
import '../core/network/api_client.dart';
import '../l10n/app_localizations.dart';

/// The sentence a screen shows for a failed request.
///
/// The server's own words when it refused something; the app's translation
/// when the request never got an answer (docs/DESIGN_SYSTEM.md: say what
/// happened and what to do).
String apiErrorText(AppLocalizations l10n, ApiException error) =>
    switch (error.kind) {
      ApiErrorKind.unreachable => l10n.errorNetwork,
      ApiErrorKind.cancelled => l10n.errorCancelled,
      ApiErrorKind.sessionExpired => l10n.errorSessionExpired,
      // Switched off on the server: the app's own three-part sentence, and for
      // the kiosk's app QR the way that still works (T-148).
      ApiErrorKind.server when error.isFeatureOff =>
        error.feature == FeatureKey.mobilePaymentAtKiosk
            ? l10n.featureOffKioskPayment
            : l10n.errorFeatureOff,
      ApiErrorKind.server =>
        error.message.isNotEmpty ? error.message : l10n.errorServer,
    };
