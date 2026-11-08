import '../../core/theme/tokens.dart';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/features/entry_points.dart';
import '../../core/features/feature_flags.dart';
import '../../core/features/readiness.dart';
import '../../core/l10n/language_controller.dart';
import '../../core/router/app_router.dart';
import '../../core/router/back_stack.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_design.dart';
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
    final tokens = context.tokens;

    return Scaffold(
      appBar: neoTabAppBar(context, title: l10n.navProfile),
      body: ListView(
        padding: const EdgeInsets.all(Spacing.x4),
        children: [
          // Who is signed in (T-342; the design's screen 14).
          Row(
            children: [
              CircleAvatar(
                radius: 32,
                backgroundColor: tokens.brand100,
                child: Text(
                  initialsOf(user?.fullName, user?.email),
                  style: theme.textTheme.titleLarge?.copyWith(
                    color: tokens.brand500,
                    fontWeight: FontWeights.semibold,
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
                      style: theme.textTheme.titleLarge?.copyWith(
                        fontWeight: FontWeights.semibold,
                      ),
                    ),
                    if (_hasName(user?.fullName))
                      Text(
                        user?.email ?? '',
                        style: theme.textTheme.bodyMedium?.copyWith(
                          color: tokens.neutral600,
                        ),
                      ),
                  ],
                ),
              ),
            ],
          ),
          const SizedBox(height: Spacing.x4),

          // The shareable code, which is how a file is sent to another user
          // (plan section 5). Copied with one tap: it is meant to be passed on.
          if (user != null && Readiness.fileSharing) ...[
            Card(
              margin: EdgeInsets.zero,
              color: tokens.brand50,
              child: Padding(
                padding: const EdgeInsets.fromLTRB(
                  Spacing.x4,
                  Spacing.x3,
                  Spacing.x2,
                  Spacing.x3,
                ),
                child: Row(
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            l10n.profilePublicId,
                            style: theme.textTheme.bodySmall?.copyWith(
                              color: tokens.neutral600,
                            ),
                          ),
                          Text(
                            user.publicId,
                            style: theme.textTheme.titleMedium?.copyWith(
                              fontWeight: FontWeights.semibold,
                            ),
                          ),
                        ],
                      ),
                    ),
                    IconButton(
                      tooltip: l10n.profileCopy,
                      icon: Icon(Icons.copy_outlined, color: tokens.brand500),
                      onPressed: () async {
                        await Clipboard.setData(
                          ClipboardData(text: user.publicId),
                        );
                        if (context.mounted) {
                          ScaffoldMessenger.of(context).showSnackBar(
                            SnackBar(content: Text(l10n.profileCopied)),
                          );
                        }
                      },
                    ),
                  ],
                ),
              ),
            ),
            const SizedBox(height: Spacing.x4),
          ],

          _Group(
            children: [
              if (Readiness.wallet)
                ListTile(
                  leading: const Icon(Icons.receipt_long_outlined),
                  title: Text(l10n.profileReceipts),
                  trailing: const Icon(Icons.chevron_right),
                  onTap: () => context.push(Routes.receipts),
                ),

              // No longer a tab (T-338): reached from here, the home screen and
              // the Print tab.
              if (ref.watch(scheduledVisibleProvider))
                ListTile(
                  leading: const Icon(Icons.schedule_outlined),
                  title: Text(l10n.navScheduled),
                  trailing: const Icon(Icons.chevron_right),
                  onTap: () => context.goTab(Routes.scheduled),
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
            ],
          ),

          if (dataExport || Readiness.accountDeletion) ...[
            const SizedBox(height: Spacing.x4),
            NeoSectionHeader(title: l10n.privacySection),
          ],
          _Group(
            children: [
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
            ],
          ),
          const SizedBox(height: Spacing.x4),
          _Group(
            children: [
              ListTile(
                leading: Icon(Icons.logout, color: tokens.danger),
                title: Text(
                  l10n.profileSignOut,
                  style: TextStyle(color: tokens.danger),
                ),
                onTap: () =>
                    ref.read(authControllerProvider.notifier).signOut(),
              ),
            ],
          ),
        ],
      ),
    );
  }

  static bool _hasName(String? fullName) =>
      fullName != null && fullName.trim().isNotEmpty;

  static String _displayName(String? fullName, String? email) =>
      _hasName(fullName) ? fullName! : (email ?? '');
}

/// Rows on one card, a hairline between them (T-342).
class _Group extends StatelessWidget {
  const _Group({required this.children});

  final List<Widget> children;

  @override
  Widget build(BuildContext context) {
    if (children.isEmpty) {
      return const SizedBox.shrink();
    }

    return Card(
      margin: EdgeInsets.zero,
      clipBehavior: Clip.antiAlias,
      child: Column(
        children: [
          for (var i = 0; i < children.length; i++) ...[
            if (i > 0) const Divider(height: 1, indent: Spacing.x16),
            children[i],
          ],
        ],
      ),
    );
  }
}
