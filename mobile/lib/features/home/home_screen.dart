import '../../core/theme/tokens.dart';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../l10n/app_localizations.dart';

/// The home tab (plan section 8.2).
///
/// The balance card, the QR scanner and the job list arrive with the wallet
/// and print flows; what is settled here is the layout and that the signed-in
/// user's own details are on screen.
class HomeScreen extends ConsumerWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final user = ref.watch(authControllerProvider).user;

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
                  const SizedBox(height: Spacing.x4),
                  Row(
                    children: [
                      Expanded(
                        child: _Metric(
                          label: l10n.homeBalance,
                          // The wallet arrives in step 4; a dash is honest,
                          // a zero would not be.
                          value: '—',
                        ),
                      ),
                      Expanded(
                        child: _Metric(label: l10n.homeBonusPages, value: '—'),
                      ),
                    ],
                  ),
                ],
              ),
            ),
          ),
          const SizedBox(height: Spacing.x4),

          FilledButton.icon(
            onPressed: null,
            icon: const Icon(Icons.qr_code_scanner),
            label: Text(l10n.homeScanQr),
          ),
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
      ),
    );
  }
}

class _Metric extends StatelessWidget {
  const _Metric({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          label,
          style: theme.textTheme.bodySmall?.copyWith(
            color: theme.colorScheme.onPrimaryContainer.withValues(alpha: 0.7),
          ),
        ),
        Text(
          value,
          style: theme.textTheme.headlineSmall?.copyWith(
            fontWeight: FontWeight.bold,
            color: theme.colorScheme.onPrimaryContainer,
          ),
        ),
      ],
    );
  }
}
