import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/app_version.dart';
import '../../core/network/update_required.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../../shared/widgets/neo_primary_button.dart';

/// "Update the app" (T-492): the backend no longer serves this version.
///
/// Full screen, no navigation (docs/DESIGN_SYSTEM.md 5.2). "Check again"
/// lets the app ask the server once more - the minimum may have come down,
/// or the app been updated in the meantime.
class UpdateRequiredScreen extends ConsumerWidget {
  const UpdateRequiredScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);

    return Scaffold(
      body: SafeArea(
        child: NeoEmptyState(
          icon: Icons.system_update,
          message: l10n.updateRequiredTitle,
          hint: l10n.updateRequiredBody(appVersion),
          action: NeoPrimaryButton(
            key: const Key('update-required-retry'),
            label: l10n.updateRequiredRetry,
            icon: Icons.refresh,
            onPressed: () =>
                ref.read(updateRequiredProvider.notifier).state = false,
          ),
        ),
      ),
    );
  }
}
