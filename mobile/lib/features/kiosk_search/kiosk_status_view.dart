import 'package:flutter/material.dart';

import '../../core/format/formats.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_status_chip.dart';
import 'kiosk_models.dart';

/// How a kiosk's status reads: tone, icon and words together, the same in the
/// list, on the map and in the legend (docs/DESIGN_SYSTEM.md, "Mobil: kiosklar").
extension KioskStatusView on KioskStatus {
  StatusTone get tone => switch (this) {
    KioskStatus.online => StatusTone.success,
    KioskStatus.paused || KioskStatus.maintenance => StatusTone.warning,
    KioskStatus.offline => StatusTone.danger,
  };

  IconData get icon => switch (this) {
    KioskStatus.online => Icons.check_circle_outline,
    KioskStatus.paused => Icons.pause_circle_outline,
    KioskStatus.maintenance => Icons.build_outlined,
    KioskStatus.offline => Icons.link_off,
  };

  String label(AppLocalizations l10n) => switch (this) {
    KioskStatus.online => l10n.kioskStatusOnline,
    KioskStatus.paused => l10n.kioskStatusPaused,
    KioskStatus.maintenance => l10n.kioskStatusMaintenance,
    KioskStatus.offline => l10n.kioskStatusOffline,
  };
}

class KioskStatusChip extends StatelessWidget {
  const KioskStatusChip({super.key, required this.status});

  final KioskStatus status;

  @override
  Widget build(BuildContext context) => NeoStatusChip(
    label: status.label(AppLocalizations.of(context)),
    tone: status.tone,
    icon: status.icon,
  );
}

/// `0,5 km` / `850 m`, in the reader's language.
String distanceText(BuildContext context, double km) {
  final l10n = AppLocalizations.of(context);
  final distance = formatDistance(
    km,
    language: Localizations.localeOf(context).languageCode,
  );

  return distance.kilometres
      ? l10n.kioskDistanceKm(distance.value)
      : l10n.kioskDistanceM(distance.value);
}
