import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../features/auth/confirm_email_screen.dart';
import '../../features/auth/register_screen.dart';
import '../../features/auth/sign_in_screen.dart';
import '../../features/auth/splash_screen.dart';
import '../../features/consents/consents_screen.dart';
import '../../features/files/camera_capture_screen.dart';
import '../../features/files/file_models.dart';
import '../../features/files/file_preview_screen.dart';
import '../../features/files/files_screen.dart';
import '../../features/home/home_screen.dart';
import '../../features/jobs/claims_screen.dart';
import '../../features/payments/pay_screen.dart';
import '../../features/payments/payment_models.dart';
import '../../features/payments/scan_screen.dart';
import '../../features/receipts/receipts_screen.dart';
import '../../features/wallet/wallet_screen.dart';
import '../../features/kiosk_search/kiosk_detail_screen.dart';
import '../../features/kiosk_search/kiosk_models.dart';
import '../../features/kiosk_search/kiosk_search_screen.dart';
import '../../features/session/pick_file_screen.dart';
import '../../features/session/print_options_screen.dart';
import '../../features/profile/profile_screen.dart';
import '../../features/schedule/schedule_screen.dart';
import '../../shared/widgets/bottom_nav_shell.dart';
import '../auth/auth_controller.dart';
import '../auth/auth_models.dart';
import '../features/readiness.dart';

/// Route paths, in one place so a rename cannot leave a dangling link.
abstract final class Routes {
  static const splash = '/';
  static const signIn = '/sign-in';
  static const register = '/register';
  static const confirmEmail = '/confirm-email';
  static const consents = '/consents';

  static const home = '/home';
  static const kiosks = '/kiosks';
  static String kiosk(String code) => '/kiosk/$code';
  static const sessionFiles = '/session/files';
  static const sessionPrint = '/session/print';
  static const files = '/files';
  static const filesCamera = '/files/camera';
  static String filePreview(String id) => '/files/$id';
  static const scheduled = '/scheduled';
  static const profile = '/profile';

  static const scan = '/scan';
  static String pay(String paymentId) => '/pay/$paymentId';
  static const wallet = '/wallet';
  static const receipts = '/receipts';
  static const claims = '/claims';
}

final routerProvider = Provider<GoRouter>((ref) {
  final notifier = ValueNotifier<AuthStatus>(AuthStatus.restoring);

  ref.listen<AuthState>(authControllerProvider, (previous, next) {
    notifier.value = next.status;
  }, fireImmediately: true);

  ref.onDispose(notifier.dispose);

  return GoRouter(
    initialLocation: Routes.splash,
    refreshListenable: notifier,

    /// The one place the sign-in gates are enforced (plan section 2.8).
    ///
    /// Deep links land here too, so a link into the app cannot skip email
    /// confirmation or an outstanding consent (plan section 8.3).
    redirect: (context, state) {
      // The kiosk's "pay with the app" QR read by another scanner arrives as
      // neoprint://pay/{id}; it becomes the app's own route, and the gates
      // below still apply to it (plan section 8.3).
      final payLink = payLinkLocation(state.uri);
      if (payLink != null) {
        return payLink;
      }

      final status = ref.read(authControllerProvider).status;
      final location = state.matchedLocation;

      switch (status) {
        case AuthStatus.restoring:
          return location == Routes.splash ? null : Routes.splash;

        case AuthStatus.signedOut:
          const allowed = {Routes.signIn, Routes.register};
          return allowed.contains(location) ? null : Routes.signIn;

        case AuthStatus.awaitingEmailConfirmation:
          return location == Routes.confirmEmail ? null : Routes.confirmEmail;

        case AuthStatus.awaitingConsents:
          return location == Routes.consents ? null : Routes.consents;

        case AuthStatus.signedIn:
          // Nobody should sit on a sign-in screen while signed in.
          const authScreens = {
            Routes.splash,
            Routes.signIn,
            Routes.register,
            Routes.confirmEmail,
            Routes.consents,
          };
          return authScreens.contains(location) ? Routes.home : null;
      }
    },

    routes: [
      GoRoute(path: Routes.splash, builder: (_, __) => const SplashScreen()),
      GoRoute(path: Routes.signIn, builder: (_, __) => const SignInScreen()),
      GoRoute(
        path: Routes.register,
        builder: (_, __) => const RegisterScreen(),
      ),
      GoRoute(
        path: Routes.confirmEmail,
        builder: (_, __) => const ConfirmEmailScreen(),
      ),
      GoRoute(
        path: Routes.consents,
        builder: (_, __) => const ConsentsScreen(),
      ),

      // Full screen, above the tabs: paying and the money screens are a task
      // of their own with a way back, not a tab (plan section 8.2).
      if (Readiness.kioskPayment) ...[
        GoRoute(path: Routes.scan, builder: (_, __) => const ScanScreen()),
        GoRoute(
          path: '/pay/:id',
          builder: (_, state) =>
              PayScreen(paymentId: state.pathParameters['id']!),
        ),
      ],
      if (Readiness.wallet) ...[
        GoRoute(path: Routes.wallet, builder: (_, __) => const WalletScreen()),
        GoRoute(
          path: Routes.receipts,
          builder: (_, __) => const ReceiptsScreen(),
        ),
      ],
      // A kiosk found in the search, or opened by its code (plan section 8.3,
      // neoprint://kiosk/{code}).
      if (Readiness.kioskSearch)
        GoRoute(
          path: '/kiosk/:code',
          builder: (_, state) => KioskDetailScreen(
            idOrCode: state.pathParameters['code']!,
            initial: state.extra is KioskSummary
                ? state.extra! as KioskSummary
                : null,
          ),
        ),
      // Printing in a session: choose the file, then its options (plan section 9.3).
      if (Readiness.mobileSession) ...[
        GoRoute(
          path: Routes.sessionFiles,
          builder: (_, __) => const PickFileScreen(),
        ),
        GoRoute(
          path: Routes.sessionPrint,
          // Opened with the file picked; without it there is nothing to print.
          redirect: (_, state) =>
              state.extra is UserFile ? null : Routes.sessionFiles,
          builder: (_, state) =>
              PrintOptionsScreen(file: state.extra! as UserFile),
        ),
      ],
      if (Readiness.printJobs)
        GoRoute(path: Routes.claims, builder: (_, __) => const ClaimsScreen()),

      // The five bottom-nav destinations from plan section 8.1, in a shell so
      // the bar keeps its state as the tabs change.
      StatefulShellRoute.indexedStack(
        builder: (context, state, shell) => BottomNavShell(shell: shell),
        branches: [
          StatefulShellBranch(
            routes: [
              GoRoute(
                path: Routes.home,
                builder: (_, __) => const HomeScreen(),
              ),
            ],
          ),
          if (Readiness.kioskSearch)
            StatefulShellBranch(
              routes: [
                GoRoute(
                  path: Routes.kiosks,
                  builder: (_, __) => const KioskSearchScreen(),
                ),
              ],
            ),
          StatefulShellBranch(
            routes: [
              GoRoute(
                path: Routes.files,
                builder: (_, __) => const FilesScreen(),
                routes: [
                  // Before ':id', which would otherwise take "camera" for an id.
                  GoRoute(
                    path: 'camera',
                    builder: (_, __) => const CameraCaptureScreen(),
                  ),
                  GoRoute(
                    path: ':id',
                    // The list hands over the file it already has. Without
                    // it - a link from outside - there is nothing to show
                    // yet, so the list is where the user lands.
                    redirect: (_, state) =>
                        state.extra is UserFile ? null : Routes.files,
                    builder: (_, state) =>
                        FilePreviewScreen(file: state.extra! as UserFile),
                  ),
                ],
              ),
            ],
          ),
          if (Readiness.scheduledPrint)
            StatefulShellBranch(
              routes: [
                GoRoute(
                  path: Routes.scheduled,
                  builder: (_, __) => const ScheduleScreen(),
                ),
              ],
            ),
          StatefulShellBranch(
            routes: [
              GoRoute(
                path: Routes.profile,
                builder: (_, __) => const ProfileScreen(),
              ),
            ],
          ),
        ],
      ),
    ],
  );
});
