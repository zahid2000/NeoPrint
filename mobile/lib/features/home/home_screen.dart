import '../../core/theme/tokens.dart';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/features/readiness.dart';
import '../../core/router/app_router.dart';
import '../../l10n/app_localizations.dart';

/// The home tab (plan section 8.2).
///
/// The balance, the QR scanner and the job list arrive with the wallet and
/// print flows (step 4). Until then they are not drawn at all - a dash or a
/// disabled button would look broken (Readiness).
class HomeScreen extends ConsumerWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final user = ref.watch(currentUserProvider);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.appTitle)),
      body: ListView(
        padding: const EdgeInsets.all(Spacing.x4),
        children: [
          Card(
            color: theme.colorScheme.primaryContainer,
            child: Padding(
              padding: const EdgeInsets.all(Spacing.x4),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    user?.fullName ?? user?.email ?? '',
                    style: theme.textTheme.titleMedium?.copyWith(
                      color: theme.colorScheme.onPrimaryContainer,
                    ),
                  ),
                  const SizedBox(height: Spacing.x2),
                  Text(
                    l10n.homeFilesHint,
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: theme.colorScheme.onPrimaryContainer,
                    ),
                  ),
                ],
              ),
            ),
          ),
          const SizedBox(height: Spacing.x4),

          // What works today: the files. The one primary action on the screen.
          FilledButton.icon(
            onPressed: () => context.go(Routes.files),
            icon: const Icon(Icons.folder_outlined),
            label: Text(l10n.homeOpenFiles),
          ),

          if (Readiness.printJobs) ...[
            const SizedBox(height: Spacing.x6),
            Text(l10n.homeActiveJobs, style: theme.textTheme.titleMedium),
            const SizedBox(height: Spacing.x2),
            Card(
              child: Padding(
                padding: const EdgeInsets.all(Spacing.x6),
                child: Center(
                  child: Text(
                    l10n.homeNoActiveJobs,
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: theme.colorScheme.outline,
                    ),
                  ),
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }
}
