import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
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
    final user = ref.watch(authControllerProvider).user;

    return Scaffold(
      appBar: AppBar(title: Text(l10n.navProfile)),
      body: ListView(
        children: [
          Padding(
            padding: const EdgeInsets.all(24),
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
                const SizedBox(width: 16),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        user?.fullName ?? '—',
                        style: theme.textTheme.titleLarge,
                      ),
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
          if (user != null)
            ListTile(
              leading: const Icon(Icons.badge_outlined),
              title: Text(l10n.profilePublicId),
              subtitle: Text(user.publicId),
              trailing: const Icon(Icons.copy_outlined),
              onTap: () async {
                await Clipboard.setData(ClipboardData(text: user.publicId));

                if (context.mounted) {
                  ScaffoldMessenger.of(context).showSnackBar(
                    SnackBar(content: Text(l10n.profileCopied)),
                  );
                }
              },
            ),

          ListTile(
            leading: const Icon(Icons.language_outlined),
            title: Text(l10n.profileLanguage),
            subtitle: Text(user?.language.toUpperCase() ?? 'AZ'),
            enabled: false,
          ),

          ListTile(
            leading: const Icon(Icons.description_outlined),
            title: Text(l10n.profileLegal),
            enabled: false,
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

  static String _initial(String? value) =>
      (value == null || value.isEmpty) ? '?' : value.trim()[0].toUpperCase();
}
