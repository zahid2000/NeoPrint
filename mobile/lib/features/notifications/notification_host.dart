import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/push/push_service.dart';
import '../../core/realtime/user_hub.dart';
import '../../core/realtime/user_hub_provider.dart';
import '../../core/router/app_links.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import 'notification_models.dart';
import 'notification_repository.dart';
import 'notifications_screen.dart';

/// Push and the in-app banner for the signed-in part of the app
/// (docs/DESIGN_SYSTEM.md, "Mobil: bildirişlər və push"):
///
/// - registers this device for push, in the app's language;
/// - opens the screen of a push the user tapped (or that started the app);
/// - shows a banner for a notification that arrives while the app is open -
///   from the hub, or from FCM, whichever comes first, once.
class NotificationHost extends ConsumerStatefulWidget {
  const NotificationHost({super.key, required this.child});

  final Widget child;

  @override
  ConsumerState<NotificationHost> createState() => _NotificationHostState();
}

class _NotificationHostState extends ConsumerState<NotificationHost> {
  /// The "Çapa ver / Vaxtı dəyiş" dialog says this one.
  static const _noBanner = {'ScheduleDue'};

  static const _bannerTime = Duration(seconds: 6);

  final _shown = <String>{};
  final _subscriptions = <StreamSubscription<Object?>>[];
  StreamSubscription<HubEvent>? _hub;
  Timer? _bannerTimer;

  @override
  void initState() {
    super.initState();

    ref.listenManual<Stream<HubEvent>>(userHubEventsProvider, (_, events) {
      unawaited(_hub?.cancel());
      _hub = events
          .where((event) => event.target == 'Notification')
          .listen(
            (event) => unawaited(
              _announce(
                event.payload['notificationId'] as String?,
                event.payload['type'] as String?,
              ),
            ),
          );
    }, fireImmediately: true);

    final push = ref.read(pushPlatformProvider);
    _subscriptions
      ..add(
        push.foreground.listen(
          (e) => unawaited(_announce(e.notificationId, null)),
        ),
      )
      ..add(push.opened.listen(_open));

    WidgetsBinding.instance.addPostFrameCallback((_) async {
      if (!mounted) {
        return;
      }

      final language = Localizations.localeOf(context).languageCode;
      unawaited(
        ref
            .read(pushRegistrationProvider)
            .register(language)
            .catchError(
              (Object error) => debugPrint('Push registration: $error'),
            ),
      );

      if (await push.launchedBy() case final event?) {
        _open(event);
      }

      // The web build: a clicked push starts the app at
      // /?link=...&notificationId=... (web/firebase-messaging-sw.js).
      if (kIsWeb && Uri.base.queryParameters['link'] != null) {
        _open(
          PushEvent(
            notificationId: Uri.base.queryParameters['notificationId'],
            link: Uri.base.queryParameters['link'],
          ),
        );
      }
    });
  }

  @override
  void dispose() {
    _bannerTimer?.cancel();
    unawaited(_hub?.cancel());
    for (final subscription in _subscriptions) {
      unawaited(subscription.cancel());
    }
    super.dispose();
  }

  void _open(PushEvent event) {
    if (!mounted) {
      return;
    }

    if (event.notificationId case final id?) {
      unawaited(
        ref
            .read(notificationsProvider.notifier)
            .markRead(id)
            .catchError((Object _) {}),
      );
    }

    final location = event.link == null
        ? null
        : appLinkLocation(Uri.parse(event.link!));
    if (location != null) {
      openAppLocation(context, location);
    }
  }

  Future<void> _announce(String? id, String? type) async {
    if (id == null || _noBanner.contains(type) || !_shown.add(id)) {
      return;
    }

    // The text in the app's language comes with the list.
    await ref.read(notificationsProvider.notifier).refresh();
    final item = ref
        .read(notificationsProvider)
        .valueOrNull
        ?.items
        .where((n) => n.id == id)
        .firstOrNull;

    if (!mounted || item == null || _noBanner.contains(item.type)) {
      return;
    }

    _showBanner(item);
  }

  void _showBanner(AppNotification item) {
    final l10n = AppLocalizations.of(context);
    final messenger = ScaffoldMessenger.of(context);
    final theme = Theme.of(context);
    final (icon, tone) = notificationVisual(item.type);
    final colours = context.tokens.forTone(tone);

    void close() {
      _bannerTimer?.cancel();
      messenger.hideCurrentMaterialBanner();
    }

    messenger
      ..hideCurrentMaterialBanner()
      ..showMaterialBanner(
        MaterialBanner(
          leading: CircleAvatar(
            backgroundColor: colours.background,
            child: Icon(icon, color: colours.foreground),
          ),
          content: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(item.title, style: theme.textTheme.titleSmall),
              const SizedBox(height: Spacing.x1),
              Text(item.body, maxLines: 2, overflow: TextOverflow.ellipsis),
            ],
          ),
          actions: [
            if (item.link != null)
              TextButton(
                onPressed: () {
                  close();
                  openNotification(context, ref, item);
                },
                child: Text(l10n.notificationOpen),
              ),
            TextButton(onPressed: close, child: Text(l10n.commonClose)),
          ],
        ),
      );

    _bannerTimer?.cancel();
    _bannerTimer = Timer(_bannerTime, close);
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
