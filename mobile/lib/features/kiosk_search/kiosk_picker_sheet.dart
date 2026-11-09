import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_client.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import 'kiosk_models.dart';
import 'kiosk_search_controller.dart';
import 'kiosk_status_view.dart';

/// Opens the kiosk choice as a bottom sheet; null when nothing was chosen.
///
/// [onlyWorking]: printing now (T-356) - a kiosk that does not take orders
/// is listed with its status but cannot be chosen.
Future<KioskSummary?> showKioskPicker(
  BuildContext context, {
  bool onlyWorking = false,
}) => showModalBottomSheet<KioskSummary>(
  context: context,
  isScrollControlled: true,
  builder: (_) => KioskPickerSheet(onlyWorking: onlyWorking),
);

/// The kiosks to choose from: the public search, nearest or by name.
class KioskPickerSheet extends ConsumerWidget {
  const KioskPickerSheet({super.key, this.onlyWorking = false});

  final bool onlyWorking;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final kiosks = ref.watch(_kioskChoicesProvider);

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              l10n.prepareKioskSheetTitle,
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: Spacing.x3),
            Flexible(
              child: switch (kiosks) {
                AsyncData(:final value) when value.isEmpty => Text(
                  l10n.kioskEmpty,
                ),
                AsyncData(:final value) => ListView(
                  shrinkWrap: true,
                  children: [
                    for (final kiosk in value)
                      ListTile(
                        contentPadding: EdgeInsets.zero,
                        title: Text(
                          l10n.sessionBannerKiosk(kiosk.code, kiosk.name),
                        ),
                        subtitle: Text(kiosk.address),
                        trailing: KioskStatusChip(status: kiosk.status),
                        enabled:
                            !onlyWorking || kiosk.status == KioskStatus.online,
                        onTap: () => Navigator.pop(context, kiosk),
                      ),
                  ],
                ),
                AsyncError(:final error) => Text(
                  error is ApiException
                      ? apiErrorText(l10n, error)
                      : l10n.errorServer,
                ),
                _ => const Center(child: CircularProgressIndicator()),
              },
            ),
          ],
        ),
      ),
    );
  }
}

final _kioskChoicesProvider = FutureProvider.autoDispose<List<KioskSummary>>(
  (ref) => ref.watch(kioskRepositoryProvider).search(),
);
