import '../../core/theme/tokens.dart';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/features/feature_flags.dart';
import '../../core/features/readiness.dart';
import '../../core/l10n/language_controller.dart';
import '../../core/router/app_router.dart';
import '../../l10n/app_localizations.dart';
import 'language_sheet.dart';

/// The profile tab (plan section 8.2).
///
/// The account, the money screens, the language and the legal documents
/// (T-152), privacy, and signing out.
class ProfileScreen extends ConsumerWidget {
  const ProfileScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final dataExport =
        Readiness.dataExport &&
        ref.watch(featureOnProvider(FeatureKey.dataExport));
    final referral =
        Readiness.referral && ref.watch(featureOnProvider(FeatureKey.referral));
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

          // The referral code, its link and what it earned (plan section 2.6).
          if (referral)
            ListTile(
              leading: const Icon(Icons.card_giftcard_outlined),
              title: Text(l10n.referralTitle),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push(Routes.referral),
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
              subtitle: Text(
                languageSummary(l10n, ref.watch(appLanguageProvider)),
              ),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => showLanguageSheet(context),
            ),

          // The app's PIN and the fingerprint or face (T-270).
          ListTile(
            leading: const Icon(Icons.lock_outline),
            title: Text(l10n.securityTitle),
            trailing: const Icon(Icons.chevron_right),
            onTap: () => context.push(Routes.security),
          ),

          if (Readiness.legalDocuments)
            ListTile(
              leading: const Icon(Icons.description_outlined),
              title: Text(l10n.profileLegal),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push(Routes.legalList),
            ),

          if (dataExport || Readiness.accountDeletion) ...[
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

          if (dataExport)
            ListTile(
              leading: const Icon(Icons.download_outlined),
              title: Text(l10n.exportTitle),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push(Routes.privacyExport),
            ),

          if (Readiness.accountDeletion)
            ListTile(
              leading: Icon(
                Icons.person_remove_outlined,
                color: theme.colorScheme.error,
              ),
              title: Text(
                l10n.deleteTitle,
                style: TextStyle(color: theme.colorScheme.error),
              ),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push(Routes.privacyDelete),
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
