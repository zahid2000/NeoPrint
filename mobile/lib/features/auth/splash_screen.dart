import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';

/// Shown while the stored refresh token is being checked.
///
/// The router keeps the app here until the auth controller reports a state, so
/// nobody sees a sign-in screen flash past when they were already signed in.
///
/// When the server cannot be reached the app stays here too (T-269): the
/// stored session may be perfectly good, so the user is told what is wrong
/// and can ask again, instead of being sent to type a password.
class SplashScreen extends ConsumerWidget {
  const SplashScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final stalled = ref.watch(
      authControllerProvider.select((state) => state.restoreStalled),
    );

    return Scaffold(
      body: Center(
        child: Padding(
          padding: const EdgeInsets.all(Spacing.x6),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Text(
                'NeoPrint',
                style: theme.textTheme.displaySmall?.copyWith(
                  fontWeight: FontWeight.bold,
                  color: theme.colorScheme.primary,
                ),
              ),
              const SizedBox(height: Spacing.x8),
              if (stalled) ...[
                Icon(
                  Icons.cloud_off_outlined,
                  color: theme.colorScheme.onSurfaceVariant,
                ),
                const SizedBox(height: Spacing.x3),
                Text(
                  AppLocalizations.of(context).splashUnreachable,
                  textAlign: TextAlign.center,
                  style: theme.textTheme.bodyMedium,
                ),
                const SizedBox(height: Spacing.x4),
                FilledButton(
                  onPressed: () =>
                      ref.read(authControllerProvider.notifier).restore(),
                  child: Text(AppLocalizations.of(context).commonRetry),
                ),
              ] else
                const CircularProgressIndicator(),
            ],
          ),
        ),
      ),
    );
  }
}
