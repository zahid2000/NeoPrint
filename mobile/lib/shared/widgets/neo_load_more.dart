import 'dart:async';

import 'package:flutter/material.dart';

import '../../core/network/api_client.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../api_error_text.dart';
import '../paging.dart';

/// Asks for the next part of a [Paged] list by itself when the list nears
/// its end (T-413, docs/DESIGN_SYSTEM.md section 5.2): no "Daha çox" button.
/// A list too short to scroll - or one whose tab hides the rows loaded so
/// far - asks again until it fills the screen or the server has no more.
class NeoLoadMore extends StatefulWidget {
  const NeoLoadMore({
    super.key,
    required this.paged,
    required this.onMore,
    required this.child,
  });

  /// How close to the end, in logical pixels, the next part is asked for.
  static const threshold = 400.0;

  final Paged<Object?>? paged;
  final Future<void> Function() onMore;

  /// The scrollable list.
  final Widget child;

  @override
  State<NeoLoadMore> createState() => _NeoLoadMoreState();
}

class _NeoLoadMoreState extends State<NeoLoadMore> {
  /// What was left below the screen the last time the list told; null until
  /// it has.
  double? _left;

  @override
  void didUpdateWidget(NeoLoadMore old) {
    super.didUpdateWidget(old);

    // Rows came in but the list did not grow (a tab hides them): the end
    // is still near, so ask again once the frame is built.
    if (old.paged != widget.paged) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted && (_left ?? double.infinity) < NeoLoadMore.threshold) {
          _ask();
        }
      });
    }
  }

  void _ask() {
    final paged = widget.paged;
    if (paged == null ||
        !paged.hasMore ||
        paged.loadingMore ||
        paged.moreError != null) {
      return;
    }
    unawaited(widget.onMore().catchError((Object _) {}));
  }

  bool _onMetrics(ScrollMetrics metrics) {
    if (metrics.axis != Axis.vertical) {
      return false;
    }
    _left = metrics.extentAfter;
    if (metrics.extentAfter < NeoLoadMore.threshold) {
      _ask();
    }
    return false;
  }

  @override
  Widget build(BuildContext context) =>
      NotificationListener<ScrollMetricsNotification>(
        // The list laid out, or its rows changed: a short one asks at once.
        onNotification: (notification) => _onMetrics(notification.metrics),
        child: NotificationListener<ScrollNotification>(
          onNotification: (notification) => _onMetrics(notification.metrics),
          child: widget.child,
        ),
      );
}

/// The row under a [Paged] list: a spinner while the next part comes, the
/// reason and "Yenidən cəhd et" when it did not; nothing otherwise.
class NeoLoadMoreFooter extends StatelessWidget {
  const NeoLoadMoreFooter({
    super.key,
    required this.paged,
    required this.onRetry,
  });

  final Paged<Object?> paged;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final error = paged.moreError;

    if (error != null) {
      final l10n = AppLocalizations.of(context);
      final reason = error is ApiException
          ? apiErrorText(l10n, error)
          : l10n.errorServer;

      return Padding(
        padding: const EdgeInsets.symmetric(vertical: Spacing.x3),
        child: Column(
          children: [
            Text(
              l10n.listMoreFailed(reason),
              textAlign: TextAlign.center,
              style: Theme.of(context).textTheme.bodyMedium,
            ),
            const SizedBox(height: Spacing.x2),
            OutlinedButton(onPressed: onRetry, child: Text(l10n.commonRetry)),
          ],
        ),
      );
    }

    if (paged.loadingMore) {
      return const Padding(
        padding: EdgeInsets.all(Spacing.x4),
        child: Center(child: CircularProgressIndicator()),
      );
    }

    return const SizedBox.shrink();
  }
}
