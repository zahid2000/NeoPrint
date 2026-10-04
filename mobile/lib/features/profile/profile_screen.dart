import '../../core/theme/tokens.dart';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/features/readiness.dart';
import '../../core/router/app_router.dart';
import '../../l10n/app_localizations.dart';

/// The profile tab (plan section 8.2).
///
/// The personal QR, the language picker and the privacy actions arrive in
/// later steps; the account details and signing out work now.
class ProfileScreen extends ConsumerWidget {
  const ProfileScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final user = ref.watch(currentUserProvider);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.navProfile)),
      body: ListView(
        children: [
          Padding(
            padding: const EdgeInsets.all(Spacing.x6),
            child: Row(
              children: [
                CircleAvatar(
                  radius: 32,
                  backgroundColor: theme.colorScheme.primaryContainer,
                  child: Text(
                    _initial(user?.fullName ?? user?.email),
                    style: theme.textTheme.headlineMedium?.copyWith(
                      color: theme.colorScheme.onPrimaryContainer,
                    ),
                  ),
                ),
                const SizedBox(width: Spacing.x4),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        // A missing name falls back to the email rather
                        // than a dash; the email line then stays away.
                        _displayName(user?.fullName, user?.email),
                        style: theme.textTheme.titleLarge,
                      ),
                      if (_hasName(user?.fullName))
                        Text(
                          user?.email ?? '',
                          style: theme.textTheme.bodyMedium?.copyWith(
                            color: theme.colorScheme.outline,
                          ),
                        ),
                    ],
                  ),
                ),
              ],
            ),
          ),

          // The shareable code, which is how a file is sent to another user
          // (plan section 5). Tapping it copies it, because it is meant to be
          // passed on.
          if (user != null && Readiness.fileSharing)
            ListTile(
              leading: const Icon(Icons.badge_outlined),
              title: Text(l10n.profilePublicId),
              subtitle: Text(user.publicId),
              trailing: const Icon(Icons.copy_outlined),
              onTap: () async {
                await Clipboard.setData(ClipboardData(text: user.publicId));

                if (context.mounted) {
                  ScaffoldMessenger.of(
                    context,
                  ).showSnackBar(SnackBar(content: Text(l10n.profileCopied)));
                }
              },
            ),

          if (Readiness.wallet)
            ListTile(
              leading: const Icon(Icons.receipt_long_outlined),
              title: Text(l10n.profileReceipts),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push(Routes.receipts),
            ),

          if (Readiness.printJobs)
            ListTile(
              leading: const Icon(Icons.support_agent_outlined),
              title: Text(l10n.profileClaims),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push(Routes.claims),
            ),

          if (Readiness.languageSetting)
            ListTile(
              leading: const Icon(Icons.language_outlined),
              title: Text(l10n.profileLanguage),
              subtitle: Text(user?.language.toUpperCase() ?? 'AZ'),
            ),

          if (Readiness.legalDocuments)
            ListTile(
              leading: const Icon(Icons.description_outlined),
              title: Text(l10n.profileLegal),
            ),

          if (Readiness.dataExport || Readiness.accountDeletion) ...[
            const Divider(),
            Padding(
              padding: const EdgeInsets.fromLTRB(
                Spacing.x4,
                Spacing.x2,
                Spacing.x4,
                0,
              ),
              child: Text(
                l10n.privacySection,
                style: theme.textTheme.titleSmall,
              ),
            ),
          ],

          if (Readiness.dataExport)
            ListTile(
              leading: const Icon(Icons.download_outlined),
              title: Text(l10n.exportTitle),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push(Routes.privacyExport),
            ),

          const Divider(),

          ListTile(
            leading: Icon(Icons.logout, color: theme.colorScheme.error),
            title: Text(
              l10n.profileSignOut,
              style: TextStyle(color: theme.colorScheme.error),
            ),
            onTap: () => ref.read(authControllerProvider.notifier).signOut(),
          ),
        ],
      ),
    );
  }

  static bool _hasName(String? fullName) =>
      fullName != null && fullName.trim().isNotEmpty;

  static String _displayName(String? fullName, String? email) =>
      _hasName(fullName) ? fullName! : (email ?? '');

  static String _initial(String? value) =>
      (value == null || value.isEmpty) ? '?' : value.trim()[0].toUpperCase();
}
