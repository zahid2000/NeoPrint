import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/features/feature_flags.dart';
import '../../core/features/readiness.dart';
import '../../core/router/app_router.dart';
import '../files/file_models.dart';
import '../session/session_controller.dart';

/// Where "Çap parametrləri" on a file's preview leads (T-347): the step
/// after choosing a file is always its print settings.
///
/// - At a kiosk now (an open session): that kiosk's settings, printed there.
/// - Otherwise "Kiosk üçün hazırla": the settings with a kiosk to choose,
///   paid now or at the kiosk, collected with the personal QR (plan 9.4).
/// - With that switched off, a scheduled print at a chosen kiosk and time.
///
/// Null when none of them is on for this user - the button is not shown.
final printNextRouteProvider = Provider.autoDispose<String?>((ref) {
  final session = Readiness.mobileSession
      ? ref.watch(sessionProvider).valueOrNull
      : null;

  if (session != null &&
      ref.watch(featureOnProvider(FeatureKey.mobileSessionPrint))) {
    return Routes.sessionPrint;
  }

  if (Readiness.personalQr &&
      ref.watch(featureOnProvider(FeatureKey.userQrPrint))) {
    return Routes.prepare;
  }

  if (Readiness.scheduledPrint &&
      ref.watch(featureOnProvider(FeatureKey.scheduledPrint))) {
    return Routes.scheduleNew;
  }

  return null;
});

/// Whether [file] can go on to printing now.
bool canPrint(UserFile file) => file.status == FileStatus.ready;
