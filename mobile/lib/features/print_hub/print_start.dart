import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/features/feature_flags.dart';
import '../../core/features/readiness.dart';
import '../../core/router/app_router.dart';
import '../files/file_models.dart';
import '../kiosk_search/kiosk_models.dart';
import '../kiosk_search/kiosk_search_controller.dart';
import '../session/session_controller.dart';

/// How a file goes to print from the phone (T-356).
enum PrintMode {
  /// At a working kiosk now: a session there, its print settings, paid, and
  /// the job is in its queue (plan section 9.3).
  now,

  /// "Kiosk üçün hazırla": collected later with the personal QR (plan 9.4).
  prepare,
}

/// "Çapa göndər" and the preview's "Çap parametrləri" (T-356, T-376).
///
/// With a kiosk session open the file goes to that kiosk's settings. Else it
/// goes straight to the print settings, where "İndi çap et" or "Hazırla" is
/// chosen above the kiosk - no sheet asks first. With neither switched on a
/// scheduled print is what is left.
///
/// [replace] swaps the current screen for the next one (the upload screen
/// does). Returns whether the file went on to a next screen.
Future<bool> startPrint(
  BuildContext context,
  WidgetRef ref,
  UserFile file, {
  bool replace = false,
}) async {
  void go(String route) => replace
      ? context.pushReplacement(route, extra: file)
      : context.push(route, extra: file);

  final canNow = printNowOn(ref);
  final canPrepare = prepareOn(ref);
  final session = canNow ? ref.read(sessionProvider).valueOrNull : null;

  if (session != null) {
    go(Routes.sessionPrint);
    return true;
  }

  if (canNow || canPrepare) {
    go(Routes.prepare);
    return true;
  }

  if (Readiness.scheduledPrint &&
      ref.read(featureOnProvider(FeatureKey.scheduledPrint))) {
    go(Routes.scheduleNew);
    return true;
  }

  return false;
}

/// "İndi çap et" is switched on: a session at a working kiosk (plan 9.3).
bool printNowOn(WidgetRef ref) =>
    Readiness.mobileSession &&
    ref.read(featureOnProvider(FeatureKey.mobileSessionPrint));

/// "Hazırla" is switched on: collected later with the personal QR (plan 9.4).
bool prepareOn(WidgetRef ref) =>
    Readiness.personalQr && ref.read(featureOnProvider(FeatureKey.userQrPrint));

/// Whether any kiosk takes orders now; false switches "Çap et" off.
final anyKioskWorkingProvider = FutureProvider.autoDispose<bool>(
  (ref) async => (await ref.watch(kioskRepositoryProvider).search()).any(
    (kiosk) => kiosk.status == KioskStatus.online,
  ),
);
