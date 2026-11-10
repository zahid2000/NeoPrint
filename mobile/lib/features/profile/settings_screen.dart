import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/features/feature_flags.dart';
import '../../core/features/readiness.dart';
import '../../core/l10n/language_controller.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_design.dart';
import 'language_sheet.dart';
import 'profile_screen.dart';

/// "Tənzimləmələr" (T-395): how the app works for this user - the language,
/// the PIN and the fingerprint - and the privacy rows, taken out of the
/// profile's list.
class SettingsScreen extends ConsumerWidget {
  const SettingsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final dataExport =
        Readiness.dataExport &&
        ref.watch(featureOnProvider(FeatureKey.dataExport));

    return Scaffold(
      appBar: AppBar(title: Text(l10n.settingsTitle)),
      body: ListView(
        padding: const EdgeInsets.all(Spacing.x4),
        children: [
          ProfileGroup(
            children: [
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
            ],
          ),

          if (dataExport || Readiness.accountDeletion) ...[
            const SizedBox(height: Spacing.x4),
            NeoSectionHeader(title: l10n.privacySection),
          ],
          ProfileGroup(
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
        ],
      ),
    );
  }
}
