import '../../core/theme/tokens.dart';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/features/feature_flags.dart';
import '../../core/features/readiness.dart';
import '../../core/router/app_router.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_design.dart';
import '../sharing/shares_screen.dart';

/// The profile tab (plan section 8.2).
///
/// The account, the money screens, the legal documents (T-152), the settings
/// (the language, the PIN, privacy - T-395), and signing out.
class ProfileScreen extends ConsumerWidget {
  const ProfileScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
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
                    // The same code as a QR, for someone to read with their
                    // phone (T-393).
                    IconButton(
                      tooltip: l10n.sharesMyQr,
                      icon: Icon(Icons.qr_code_2, color: tokens.brand500),
                      onPressed: () => showMyQrSheet(context, user.publicId),
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

          ProfileGroup(
            children: [
              // Every order, printed or waiting (T-386).
              if (Readiness.printJobs)
                ListTile(
                  leading: const Icon(Icons.history),
                  title: Text(l10n.jobsTitle),
                  trailing: const Icon(Icons.chevron_right),
                  onTap: () => context.push(Routes.jobs),
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

              // The language, the PIN and privacy (T-395).
              ListTile(
                leading: const Icon(Icons.settings_outlined),
                title: Text(l10n.settingsTitle),
                trailing: const Icon(Icons.chevron_right),
                onTap: () => context.push(Routes.settings),
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

          const SizedBox(height: Spacing.x4),
          ProfileGroup(
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
class ProfileGroup extends StatelessWidget {
  const ProfileGroup({super.key, required this.children});

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
