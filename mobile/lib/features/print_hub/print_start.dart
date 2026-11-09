import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/features/feature_flags.dart';
import '../../core/features/readiness.dart';
import '../../core/network/api_client.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_primary_button.dart';
import '../files/file_models.dart';
import '../kiosk_search/kiosk_models.dart';
import '../kiosk_search/kiosk_picker_sheet.dart';
import '../kiosk_search/kiosk_search_controller.dart';
import '../session/session_controller.dart';

/// How a file goes to print from the phone (T-356).
enum PrintMode {
  /// At a working kiosk now: a session there, its print settings, paid, and
  /// the job is in its queue (plan section 9.3).
  now,

  /// "Kiosk üçün hazırla": collected later with the personal QR (plan 9.4).
  prepare,
}

/// "Çapa göndər" and the preview's "Çap parametrləri" (T-356).
///
/// With a kiosk session open the file goes to that kiosk's settings. Else
/// the user chooses "Çap et" (the default) or "Kiosk üçün hazırla"; with
/// only one of them switched on there is nothing to choose, and with
/// neither a scheduled print is what is left.
///
/// [replace] swaps the current screen for the next one (the upload screen
/// does). Returns whether the file went on to a next screen.
Future<bool> startPrint(
  BuildContext context,
  WidgetRef ref,
  UserFile file, {
  bool replace = false,
}) async {
  void go(String route) => replace
      ? context.pushReplacement(route, extra: file)
      : context.push(route, extra: file);

  final canNow =
      Readiness.mobileSession &&
      ref.read(featureOnProvider(FeatureKey.mobileSessionPrint));
  final canPrepare =
      Readiness.personalQr &&
      ref.read(featureOnProvider(FeatureKey.userQrPrint));
  final session = canNow ? ref.read(sessionProvider).valueOrNull : null;

  if (session != null) {
    go(Routes.sessionPrint);
    return true;
  }

  final modes = [if (canNow) PrintMode.now, if (canPrepare) PrintMode.prepare];

  if (modes.isEmpty) {
    if (Readiness.scheduledPrint &&
        ref.read(featureOnProvider(FeatureKey.scheduledPrint))) {
      go(Routes.scheduleNew);
      return true;
    }
    return false;
  }

  final mode = modes.length == 1
      ? modes.single
      : await showModalBottomSheet<PrintMode>(
          context: context,
          showDragHandle: true,
          isScrollControlled: true,
          builder: (_) => const PrintModeSheet(),
        );

  if (mode == null || !context.mounted) {
    return false;
  }

  if (mode == PrintMode.prepare) {
    go(Routes.prepare);
    return true;
  }

  // "Çap et": only a kiosk that takes orders now can be chosen.
  final kiosk = await showKioskPicker(context, onlyWorking: true);
  if (kiosk == null || !context.mounted) {
    return false;
  }

  try {
    await ref.read(sessionProvider.notifier).open(kiosk.code);
  } on ApiException catch (error) {
    if (context.mounted) {
      final l10n = AppLocalizations.of(context);
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(l10n.sessionOpenFailed(apiErrorText(l10n, error))),
        ),
      );
    }
    return false;
  }

  if (!context.mounted) {
    return false;
  }

  go(Routes.sessionPrint);
  return true;
}

/// Whether any kiosk takes orders now; false switches "Çap et" off.
final anyKioskWorkingProvider = FutureProvider.autoDispose<bool>(
  (ref) async => (await ref.watch(kioskRepositoryProvider).search()).any(
    (kiosk) => kiosk.status == KioskStatus.online,
  ),
);

/// "Necə çap edək?": two cards, "Çap et" chosen at first, one main button.
/// With no kiosk working now "Çap et" is off, says why, and the
/// preparation is the choice (T-356).
class PrintModeSheet extends ConsumerStatefulWidget {
  const PrintModeSheet({super.key});

  @override
  ConsumerState<PrintModeSheet> createState() => _PrintModeSheetState();
}

class _PrintModeSheetState extends ConsumerState<PrintModeSheet> {
  PrintMode _mode = PrintMode.now;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    // Unknown (still asking, or no answer) leaves it on: the kiosk list
    // still lets only a working kiosk be chosen.
    final nowOn = ref.watch(anyKioskWorkingProvider).valueOrNull != false;
    final current = nowOn ? _mode : PrintMode.prepare;

    Widget option(
      PrintMode mode,
      IconData icon,
      String title,
      String hint, {
      bool enabled = true,
    }) {
      final chosen = current == mode;
      final tokens = context.tokens;
      final ink = enabled ? tokens.brand500 : tokens.neutral400;

      return Semantics(
        selected: chosen,
        enabled: enabled,
        button: true,
        child: Card(
          margin: EdgeInsets.zero,
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(Radii.md),
            side: BorderSide(
              color: chosen ? tokens.brand500 : tokens.neutral200,
              width: chosen ? 2 : 1,
            ),
          ),
          child: InkWell(
            borderRadius: BorderRadius.circular(Radii.md),
            onTap: enabled ? () => setState(() => _mode = mode) : null,
            child: Padding(
              padding: const EdgeInsets.all(Spacing.x4),
              child: Row(
                children: [
                  Icon(icon, color: ink),
                  const SizedBox(width: Spacing.x3),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          title,
                          style: theme.textTheme.titleSmall?.copyWith(
                            fontWeight: FontWeights.semibold,
                            color: enabled ? null : tokens.neutral400,
                          ),
                        ),
                        const SizedBox(height: Spacing.x1),
                        Text(
                          hint,
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: tokens.neutral600,
                          ),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(width: Spacing.x2),
                  Icon(
                    chosen
                        ? Icons.radio_button_checked
                        : Icons.radio_button_unchecked,
                    color: chosen ? ink : tokens.neutral400,
                  ),
                ],
              ),
            ),
          ),
        ),
      );
    }

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(
          Spacing.x4,
          0,
          Spacing.x4,
          Spacing.x4,
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(l10n.printModeTitle, style: theme.textTheme.titleLarge),
            const SizedBox(height: Spacing.x4),
            option(
              PrintMode.now,
              Icons.print_outlined,
              l10n.printModeNow,
              nowOn ? l10n.printModeNowHint : l10n.printModeNowNone,
              enabled: nowOn,
            ),
            const SizedBox(height: Spacing.x3),
            option(
              PrintMode.prepare,
              Icons.storefront_outlined,
              l10n.prepareMenu,
              l10n.printModePrepareHint,
            ),
            const SizedBox(height: Spacing.x4),
            NeoPrimaryButton(
              label: l10n.printModeContinue,
              onPressed: () => Navigator.pop(context, current),
            ),
          ],
        ),
      ),
    );
  }
}
