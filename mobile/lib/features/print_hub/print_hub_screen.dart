import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/features/entry_points.dart';
import '../../core/features/readiness.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_design.dart';
import '../../shared/widgets/neo_icon_tile.dart';
import '../payments/scan_screen.dart';
import '../sharing/shares_controller.dart';

/// The Print tab, "Yeni çap" (docs/DESIGN_SYSTEM.md, "Mobil: yeni vizual
/// dil"): only ways to print (T-353) - a file is chosen from "Fayllarım",
/// where a new one can be uploaded, and printed at a kiosk.
/// No primary button - each row is one equal choice.
class PrintHubScreen extends ConsumerWidget {
  const PrintHubScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final canScanQr = Readiness.kioskPayment && ref.watch(canScanProvider);
    final personalQr = ref.watch(personalQrVisibleProvider);
    final scheduled = ref.watch(scheduledVisibleProvider);
    final newShares = Readiness.fileSharing
        ? ref.watch(newSharesCountProvider)
        : 0;

    Widget section(String title) => Padding(
      padding: const EdgeInsets.only(top: Spacing.x6, bottom: Spacing.x2),
      child: Text(
        title,
        style: theme.textTheme.titleMedium?.copyWith(
          fontWeight: FontWeights.semibold,
        ),
      ),
    );

    final rows = <Widget>[
      Text(
        l10n.printHubSubtitle,
        style: theme.textTheme.bodyMedium?.copyWith(
          color: context.tokens.neutral600,
        ),
      ),
      section(l10n.printHubAddSection),
      NeoNavRow(
        icon: Icons.folder_outlined,
        title: l10n.printHubFromFiles,
        hint: l10n.printHubFromFilesHint,
        onTap: () => context.push(Routes.printChoose),
      ),
      if (Readiness.fileSharing)
        NeoNavRow(
          icon: Icons.move_to_inbox_outlined,
          tone: IconTileTone.warning,
          title: l10n.printHubShared,
          hint: l10n.printHubSharedHint,
          trailing: newShares > 0 ? Badge.count(count: newShares) : null,
          onTap: () => context.push(Routes.shares),
        ),
      section(l10n.printHubKioskSection),
      if (canScanQr)
        NeoNavRow(
          icon: Icons.qr_code_scanner,
          title: l10n.printHubScanQr,
          hint: l10n.printHubScanQrHint,
          onTap: () => context.push(Routes.scan),
        ),
      if (Readiness.kioskSearch)
        NeoNavRow(
          icon: Icons.place_outlined,
          tone: IconTileTone.info,
          title: l10n.printHubPickKiosk,
          hint: l10n.printHubPickKioskHint,
          onTap: () => context.push(Routes.kiosks),
        ),
      if (personalQr) ...[
        NeoNavRow(
          icon: Icons.qr_code_2,
          title: l10n.myQrTitle,
          hint: l10n.printHubMyQrHint,
          onTap: () => context.push(Routes.myQr),
        ),
        NeoNavRow(
          icon: Icons.inventory_2_outlined,
          tone: IconTileTone.success,
          title: l10n.preparedTitle,
          hint: l10n.printHubPreparedHint,
          onTap: () => context.push(Routes.prepared),
        ),
      ],
      if (scheduled)
        NeoNavRow(
          icon: Icons.schedule_outlined,
          tone: IconTileTone.warning,
          title: l10n.homeScheduled,
          hint: l10n.printHubScheduledHint,
          onTap: () => context.push(Routes.scheduled),
        ),
      // What was printed and what waits, as a history (T-386).
      if (Readiness.printJobs)
        NeoNavRow(
          icon: Icons.history,
          tone: IconTileTone.info,
          title: l10n.jobsTitle,
          hint: l10n.printHubJobsHint,
          onTap: () => context.push(Routes.jobs),
        ),
    ];

    return Scaffold(
      appBar: neoTabAppBar(context, title: l10n.printHubTitle),
      body: ListView.separated(
        padding: const EdgeInsets.all(Spacing.x4),
        itemCount: rows.length,
        // Sections bring their own space; between rows it is a small gap.
        separatorBuilder: (_, index) =>
            rows[index] is Padding || rows[index + 1] is Padding
            ? const SizedBox.shrink()
            : const SizedBox(height: Spacing.x2),
        itemBuilder: (_, index) => rows[index],
      ),
    );
  }
}
