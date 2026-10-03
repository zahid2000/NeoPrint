import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/format/formats.dart';
import '../../core/realtime/user_hub.dart';
import '../../core/realtime/user_hub_provider.dart';
import '../../l10n/app_localizations.dart';

/// The sentence a hub message deserves at the bottom of the screen, or null
/// when the lists updating by themselves is enough
/// (docs/DESIGN_SYSTEM.md section 4, "Canlı bildirişlər").
String? noticeFor(AppLocalizations l10n, HubEvent event, String language) {
  final payload = event.payload;

  switch (event.target) {
    case 'JobStatusChanged' when payload['status'] == 'Suspected':
      return l10n.noticeSuspected;

    case 'RefundStatusChanged' when payload['status'] == 'Succeeded':
      final amount = formatMoney(
        (payload['amount'] as num? ?? 0).toDouble(),
        language: language,
      );

      return switch (payload['destination']) {
        'Wallet' => l10n.noticeRefundWallet(amount),
        'Voucher' => l10n.noticeRefundVoucher(amount),
        _ => l10n.noticeRefundCard(amount),
      };

    // Only when its time ran out: closing or moving on is the user's own doing.
    case 'SessionClosed' when payload['reason'] == 'Expired':
      return l10n.noticeSessionExpired;

    default:
      return null;
  }
}

/// Shows the user hub's news as a snackbar, wherever the user is in the app.
class HubNotices extends ConsumerStatefulWidget {
  const HubNotices({super.key, required this.child});

  final Widget child;

  @override
  ConsumerState<HubNotices> createState() => _HubNoticesState();
}

class _HubNoticesState extends ConsumerState<HubNotices> {
  StreamSubscription<HubEvent>? _subscription;

  @override
  void initState() {
    super.initState();
    ref.listenManual<Stream<HubEvent>>(
      userHubEventsProvider,
      (_, events) => _listen(events),
      fireImmediately: true,
    );
  }

  void _listen(Stream<HubEvent> events) {
    unawaited(_subscription?.cancel());
    _subscription = events.listen((event) {
      if (!mounted) {
        return;
      }

      final text = noticeFor(
        AppLocalizations.of(context),
        event,
        Localizations.localeOf(context).languageCode,
      );

      if (text != null) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text(text)));
      }
    });
  }

  @override
  void dispose() {
    unawaited(_subscription?.cancel());
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
