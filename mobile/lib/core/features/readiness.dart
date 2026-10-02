/// Which parts of the app have their backend yet (plan section 12).
///
/// A part that is not ready is not shown at all: no dash for a balance that
/// does not exist, no disabled button, no "coming soon" tab. The user sees a
/// smaller app that works rather than a bigger one that looks broken
/// (docs/DESIGN_SYSTEM.md, "Hazır olmayan funksiya"). Each entry is switched on
/// in the step that delivers it; the backend feature flags (plan section 2.16)
/// then decide per user on top of this.
abstract final class Readiness {
  /// Wallet balance and bonus pages on the home card - step 4.
  static const bool wallet = true;

  /// Paying at a kiosk by scanning its QR code - step 4.
  static const bool kioskPayment = true;

  /// The user's own print jobs on the home tab - step 4.
  static const bool printJobs = true;

  /// Kiosk search and the map - step 5.
  static const bool kioskSearch = true;

  /// Scheduled printing - step 5.
  static const bool scheduledPrint = false;

  /// Sending a file to another user by their public code - step 6.
  static const bool fileSharing = false;

  /// Choosing the interface language inside the app - step 5; until then the
  /// app follows the phone's language.
  static const bool languageSetting = false;

  /// Reading the legal documents again from the profile - step 5.
  static const bool legalDocuments = false;
}
