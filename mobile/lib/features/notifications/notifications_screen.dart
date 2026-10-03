import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/router/app_links.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_empty_state.dart';
import 'notification_models.dart';
import 'notification_repository.dart';

/// The icon and tone of a notification type (docs/DESIGN_SYSTEM.md, "Mobil:
/// bildirişlər və push").
(IconData, StatusTone) notificationVisual(String type) => switch (type) {
  'JobCompleted' => (Icons.print, StatusTone.success),
  'ScheduleDue' => (Icons.alarm, StatusTone.info),
  'ScheduleAutoCancelled' => (Icons.event_busy, StatusTone.warning),
  'RefundCompleted' => (Icons.undo, StatusTone.success),
  _ => (Icons.notifications, StatusTone.neutral),
};

/// Reads it and opens its screen; with no link, reading is all.
void openNotification(
  BuildContext context,
  WidgetRef ref,
  AppNotification item,
) {
  // Reading is a nicety: a failure here must not stop the screen opening.
  unawaited(
    ref
        .read(notificationsProvider.notifier)
        .markRead(item.id)
        .catchError((Object _) {}),
  );

  final location = item.link == null
      ? null
      : appLinkLocation(Uri.parse(item.link!));
  if (location != null) {
    openAppLocation(context, location);
  }
}

/// The bell on the home screen's bar, with the unread count.
class NotificationBell extends ConsumerWidget {
  const NotificationBell({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final unread = ref.watch(unreadNotificationsProvider);

    return IconButton(
      tooltip: unread > 0
          ? l10n.notificationsBellUnread(unread)
          : l10n.notificationsTitle,
      onPressed: () => context.push(Routes.notifications),
      icon: Badge(
        isLabelVisible: unread > 0,
        backgroundColor: context.tokens.forTone(StatusTone.danger).foreground,
        label: Text(unread > 99 ? '99+' : '$unread'),
        child: const Icon(Icons.notifications_outlined),
      ),
    );
  }
}

/// Plan section 8.2, "notifications": the list, newest first.
class NotificationsScreen extends ConsumerWidget {
  const NotificationsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final notifications = ref.watch(notificationsProvider);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.notificationsTitle)),
      body: switch (notifications) {
        AsyncData(:final value) when value.items.isEmpty => NeoEmptyState(
          icon: Icons.notifications_none,
          message: l10n.notificationsEmpty,
          hint: l10n.notificationsEmptyHint,
        ),
        AsyncData(:final value) => RefreshIndicator(
          onRefresh: () => ref.read(notificationsProvider.notifier).refresh(),
          child: NotificationListener<ScrollNotification>(
            // Near the end: the older ones come in by themselves.
            onNotification: (scroll) {
              if (scroll.metrics.extentAfter < 400) {
                unawaited(
                  ref
                      .read(notificationsProvider.notifier)
                      .loadMore()
                      .catchError((Object _) {}),
                );
              }
              return false;
            },
            child: ListView.separated(
              padding: const EdgeInsets.symmetric(vertical: Spacing.x2),
              itemCount: value.items.length + (value.hasMore ? 1 : 0),
              separatorBuilder: (_, __) => const Divider(height: 1),
              itemBuilder: (context, index) => index < value.items.length
                  ? _NotificationTile(item: value.items[index])
                  : const Padding(
                      padding: EdgeInsets.all(Spacing.x4),
                      child: Center(child: CircularProgressIndicator()),
                    ),
            ),
          ),
        ),
        AsyncError(:final error) => NeoEmptyState(
          icon: Icons.cloud_off_outlined,
          message: l10n.notificationsLoadFailed(
            error is ApiException
                ? apiErrorText(l10n, error)
                : l10n.errorServer,
          ),
          action: OutlinedButton(
            onPressed: () => ref.invalidate(notificationsProvider),
            child: Text(l10n.commonRetry),
          ),
        ),
        _ => const Center(child: CircularProgressIndicator()),
      },
    );
  }
}

class _NotificationTile extends ConsumerWidget {
  const _NotificationTile({required this.item});

  final AppNotification item;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final (icon, tone) = notificationVisual(item.type);
    final colours = tokens.forTone(tone);

    return InkWell(
      onTap: () => openNotification(context, ref, item),
      child: Padding(
        padding: const EdgeInsets.symmetric(
          horizontal: Spacing.x4,
          vertical: Spacing.x3,
        ),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            CircleAvatar(
              radius: 20,
              backgroundColor: colours.background,
              child: Icon(icon, color: colours.foreground, size: 20),
            ),
            const SizedBox(width: Spacing.x3),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Expanded(
                        child: Text(
                          item.title,
                          style: theme.textTheme.titleSmall?.copyWith(
                            fontWeight: item.isRead
                                ? FontWeight.w500
                                : FontWeight.w700,
                          ),
                        ),
                      ),
                      if (!item.isRead)
                        Semantics(
                          container: true,
                          label: l10n.notificationsUnread,
                          child: Container(
                            width: Spacing.x2,
                            height: Spacing.x2,
                            decoration: BoxDecoration(
                              color: theme.colorScheme.primary,
                              shape: BoxShape.circle,
                            ),
                          ),
                        ),
                    ],
                  ),
                  const SizedBox(height: Spacing.x1),
                  Text(
                    item.body,
                    style: theme.textTheme.bodyMedium,
                    maxLines: 3,
                    overflow: TextOverflow.ellipsis,
                  ),
                  const SizedBox(height: Spacing.x1),
                  Text(
                    formatDateTime(
                      item.createdAt,
                      today: l10n.commonToday,
                      yesterday: l10n.commonYesterday,
                    ),
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: tokens.neutral600,
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}
