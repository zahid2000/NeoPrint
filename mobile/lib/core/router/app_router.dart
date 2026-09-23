import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../features/auth/confirm_email_screen.dart';
import '../../features/auth/register_screen.dart';
import '../../features/auth/sign_in_screen.dart';
import '../../features/auth/splash_screen.dart';
import '../../features/consents/consents_screen.dart';
import '../../features/files/files_screen.dart';
import '../../features/home/home_screen.dart';
import '../../features/kiosk_search/kiosk_search_screen.dart';
import '../../features/profile/profile_screen.dart';
import '../../features/schedule/schedule_screen.dart';
import '../../shared/widgets/bottom_nav_shell.dart';
import '../auth/auth_controller.dart';
import '../auth/auth_models.dart';

/// Route paths, in one place so a rename cannot leave a dangling link.
abstract final class Routes {
  static const splash = '/';
  static const signIn = '/sign-in';
  static const register = '/register';
  static const confirmEmail = '/confirm-email';
  static const consents = '/consents';

  static const home = '/home';
  static const kiosks = '/kiosks';
  static const files = '/files';
  static const scheduled = '/scheduled';
  static const profile = '/profile';
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
      GoRoute(path: Routes.register, builder: (_, __) => const RegisterScreen()),
      GoRoute(
        path: Routes.confirmEmail,
        builder: (_, __) => const ConfirmEmailScreen(),
      ),
      GoRoute(path: Routes.consents, builder: (_, __) => const ConsentsScreen()),

      // The five bottom-nav destinations from plan section 8.1, in a shell so
      // the bar keeps its state as the tabs change.
      StatefulShellRoute.indexedStack(
        builder: (context, state, shell) => BottomNavShell(shell: shell),
        branches: [
          StatefulShellBranch(
            routes: [
              GoRoute(path: Routes.home, builder: (_, __) => const HomeScreen()),
            ],
          ),
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
              ),
            ],
          ),
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
