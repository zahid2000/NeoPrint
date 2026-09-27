import '../../core/theme/tokens.dart';

import 'package:flutter/material.dart';

import '../../l10n/app_localizations.dart';

/// A tab whose content arrives in a later step.
///
/// Says so plainly rather than showing an empty page, so it is obvious the
/// screen is not broken.
class PlaceholderScreen extends StatelessWidget {
  const PlaceholderScreen({required this.title, required this.icon, super.key});

  final String title;
  final IconData icon;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context);

    return Scaffold(
      appBar: AppBar(title: Text(title)),
      body: Center(
        child: Padding(
          padding: const EdgeInsets.all(Spacing.x8),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Icon(icon, size: 64, color: theme.colorScheme.outline),
              const SizedBox(height: Spacing.x4),
              Text(
                l10n.commonComingSoon,
                textAlign: TextAlign.center,
                style: theme.textTheme.bodyLarge?.copyWith(
                  color: theme.colorScheme.outline,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
