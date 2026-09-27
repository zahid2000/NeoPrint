import '../../core/theme/tokens.dart';

import 'package:flutter/material.dart';

/// Shown while the stored refresh token is being checked.
///
/// The router keeps the app here until the auth controller reports a state, so
/// nobody sees a sign-in screen flash past when they were already signed in.
class SplashScreen extends StatelessWidget {
  const SplashScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Scaffold(
      body: Center(
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
            const CircularProgressIndicator(),
          ],
        ),
      ),
    );
  }
}
