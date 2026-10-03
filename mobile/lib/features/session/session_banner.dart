import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/network/api_client.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import 'session_controller.dart';
import 'session_models.dart';

/// The open session, just above the navigation bar on every tab
/// (docs/DESIGN_SYSTEM.md, "Mobil: sessiya ilə çap"): which kiosk, how long
/// it lasts, "Fayl seç" and closing. Nothing at all without a session.
class SessionBanner extends ConsumerWidget {
  const SessionBanner({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final session = ref.watch(sessionProvider).valueOrNull;

    return session == null
        ? const SizedBox.shrink()
        : _Banner(session: session);
  }
}

class _Banner extends ConsumerStatefulWidget {
  const _Banner({required this.session});

  final KioskSessionInfo session;

  @override
  ConsumerState<_Banner> createState() => _BannerState();
}

class _BannerState extends ConsumerState<_Banner> {
  /// The time left is in minutes; this keeps it right without redrawing every second.
  static const _tick = Duration(seconds: 15);

  /// The last minutes are told in the warning colour.
  static const _soon = Duration(minutes: 2);

  late final Timer _timer = Timer.periodic(_tick, (_) => setState(() {}));

  @override
  void dispose() {
    _timer.cancel();
    super.dispose();
  }

  Future<void> _close() async {
    try {
      await ref.read(sessionProvider.notifier).close();
    } on ApiException catch (error) {
      if (mounted) {
        final l10n = AppLocalizations.of(context);
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text(apiErrorText(l10n, error))));
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final session = widget.session;
    final left = session.expiresAt.difference(DateTime.now());
    final soon = left < _soon;
    final colours = context.tokens.forTone(
      soon ? StatusTone.warning : StatusTone.info,
    );

    return Material(
      color: colours.background,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(
          Spacing.x4,
          Spacing.x2,
          Spacing.x2,
          Spacing.x2,
        ),
        child: Row(
          children: [
            Icon(
              soon ? Icons.timer_outlined : Icons.print_outlined,
              color: colours.foreground,
            ),
            const SizedBox(width: Spacing.x3),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(
                    l10n.sessionBannerKiosk(
                      session.kioskCode,
                      session.kioskName,
                    ),
                    style: theme.textTheme.titleSmall,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                  Text(
                    left.inMinutes >= 1
                        ? l10n.sessionMinutesLeft(left.inMinutes)
                        : l10n.sessionUnderMinute,
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: colours.foreground,
                      fontFeatures: const [FontFeature.tabularFigures()],
                    ),
                  ),
                ],
              ),
            ),
            TextButton(
              onPressed: () => unawaited(context.push(Routes.sessionFiles)),
              child: Text(l10n.sessionChooseFile),
            ),
            IconButton(
              tooltip: l10n.sessionClose,
              icon: const Icon(Icons.close),
              onPressed: () => unawaited(_close()),
            ),
          ],
        ),
      ),
    );
  }
}
