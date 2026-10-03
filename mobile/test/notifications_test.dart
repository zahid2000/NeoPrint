import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:neoprint/core/push/push_service.dart';
import 'package:neoprint/core/realtime/user_hub.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/router/app_links.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/notifications/notification_host.dart';
import 'package:neoprint/features/notifications/notification_models.dart';
import 'package:neoprint/features/notifications/notification_repository.dart';
import 'package:neoprint/features/notifications/notifications_screen.dart';
import 'package:neoprint/l10n/app_localizations.dart';

/// The notifications API the test scripts.
class FakeNotifications implements NotificationRepository {
  List<AppNotification> items = [];
  final read = <String>[];

  @override
  Future<NotificationPage> list({DateTime? before, int take = 20}) async =>
      NotificationPage(
        items: items,
        unread: items.where((n) => !n.isRead).length,
        next: null,
      );

  @override
  Future<void> markRead(String id) async => read.add(id);
}

class FakePushApi implements PushDeviceApi {
  final registered = <({String token, String language, String platform})>[];
  final removed = <String>[];

  @override
  Future<void> register({
    required String token,
    required String language,
    required String platform,
  }) async =>
      registered.add((token: token, language: language, platform: platform));

  @override
  Future<void> remove(String token) async => removed.add(token);
}

class FakePushPlatform implements PushPlatform {
  FakePushPlatform({this.deviceToken = 'fcm-1', this.launch});

  final String? deviceToken;
  final PushEvent? launch;
  final refreshes = StreamController<String>.broadcast();
  final inFront = StreamController<PushEvent>.broadcast();
  final tapped = StreamController<PushEvent>.broadcast();

  @override
  Future<String?> token() async => deviceToken;

  @override
  Stream<String> get tokenRefreshed => refreshes.stream;

  @override
  Stream<PushEvent> get foreground => inFront.stream;

  @override
  Stream<PushEvent> get opened => tapped.stream;

  @override
  Future<PushEvent?> launchedBy() async => launch;

  @override
  String get platform => 'android';
}

AppNotification note(
  String id,
  String type, {
  String? link,
  bool read = false,
  String title = 'Çap hazırdır',
}) => AppNotification(
  id: id,
  type: type,
  title: title,
  body: '100001 sifarişi KSK-9002 kioskunda çap olundu.',
  link: link,
  createdAt: DateTime.now(),
  readAt: read ? DateTime.now() : null,
);

Widget app(
  Widget home, {
  required FakeNotifications notifications,
  StreamController<HubEvent>? hub,
  FakePushApi? pushApi,
  FakePushPlatform? platform,
}) => ProviderScope(
  overrides: [
    notificationRepositoryProvider.overrideWithValue(notifications),
    userHubEventsProvider.overrideWithValue(
      hub?.stream ?? const Stream.empty(),
    ),
    userHubReconnectedProvider.overrideWithValue(const Stream.empty()),
    pushDeviceApiProvider.overrideWithValue(pushApi ?? FakePushApi()),
    pushPlatformProvider.overrideWithValue(platform ?? FakePushPlatform()),
  ],
  child: MaterialApp.router(
    theme: AppTheme.light(),
    locale: const Locale('az'),
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    routerConfig: GoRouter(
      routes: [
        GoRoute(path: '/', builder: (_, __) => home),
        GoRoute(path: '/home', builder: (_, __) => const Text('home tab')),
        GoRoute(
          path: '/scheduled',
          builder: (_, __) => const Text('scheduled tab'),
        ),
        GoRoute(path: '/wallet', builder: (_, __) => const Text('wallet')),
        GoRoute(
          path: '/notifications',
          builder: (_, __) => const NotificationsScreen(),
        ),
      ],
    ),
  ),
);

void main() {
  group('links into the app (plan section 8.3)', () {
    test('every link opens its screen, anything else is not ours', () {
      final cases = {
        'neoprint://pay/3f0e': '/pay/3f0e',
        'neoprint://kiosk/ksk-9002': '/kiosk/KSK-9002',
        'neoprint://voucher/rf-ab12-cd34': '/wallet?voucher=RF-AB12-CD34',
        'https://neoprint.az/r/np7k2m9q': '/register?ref=NP7K2M9Q',
        '/r/NP7K2M9Q': '/register?ref=NP7K2M9Q',
        'neoprint://jobs/1b2c': '/home',
        'neoprint://schedules/9a8b': '/scheduled',
        'neoprint://wallet': '/wallet',
        'neoprint://notifications': '/notifications',
      };
      cases.forEach(
        (link, route) =>
            expect(appLinkLocation(Uri.parse(link)), route, reason: link),
      );

      for (final foreign in [
        'https://evil.example/r/NP1',
        'neoprint://pay',
        'neoprint://kiosk/a b',
        'neoprint://admin/1',
        '/home',
      ]) {
        expect(appLinkLocation(Uri.parse(foreign)), isNull, reason: foreign);
      }
    });
  });

  group('the notifications screen', () {
    testWidgets('lists them, marks the unread, and a tap reads and opens it', (
      tester,
    ) async {
      final semantics = tester.ensureSemantics();
      final notifications = FakeNotifications()
        ..items = [
          note(
            'n1',
            'ScheduleAutoCancelled',
            link: 'neoprint://schedules/s1',
            title: 'Planlanmış çap ləğv olundu',
          ),
          note('n2', 'JobCompleted', read: true),
        ];

      await tester.pumpWidget(
        app(
          const Scaffold(body: NotificationBell()),
          notifications: notifications,
        ),
      );
      await tester.pumpAndSettle();

      // The bell carries the unread count.
      expect(find.text('1'), findsOneWidget);
      expect(find.byTooltip('Bildirişlər, 1 oxunmamış'), findsOneWidget);

      await tester.tap(find.byType(NotificationBell));
      await tester.pumpAndSettle();
      expect(find.text('Bildirişlər'), findsOneWidget);
      expect(find.text('Planlanmış çap ləğv olundu'), findsOneWidget);
      expect(find.bySemanticsLabel('Oxunmayıb'), findsOneWidget);

      await tester.tap(find.text('Planlanmış çap ləğv olundu'));
      await tester.pumpAndSettle();
      expect(notifications.read, ['n1']);
      expect(find.text('scheduled tab'), findsOneWidget);
      semantics.dispose();
    });

    testWidgets('an empty list says what will come here', (tester) async {
      await tester.pumpWidget(
        app(const NotificationsScreen(), notifications: FakeNotifications()),
      );
      await tester.pumpAndSettle();

      expect(find.text('Hələ bildiriş yoxdur'), findsOneWidget);
    });
  });

  group('push and the in-app banner', () {
    testWidgets(
      'the device is registered in the app language, a new one shows a banner once, the due one does not',
      (tester) async {
        final hub = StreamController<HubEvent>.broadcast();
        final pushApi = FakePushApi();
        final notifications = FakeNotifications();

        await tester.pumpWidget(
          app(
            const NotificationHost(child: Scaffold(body: Text('home'))),
            notifications: notifications,
            hub: hub,
            pushApi: pushApi,
          ),
        );
        await tester.pumpAndSettle();

        expect(pushApi.registered.single, (
          token: 'fcm-1',
          language: 'az',
          platform: 'android',
        ));

        // The scheduled print's time: the dialog says it, not a banner.
        notifications.items = [
          note('due', 'ScheduleDue', title: 'Planlanmış çapın vaxtı çatdı'),
        ];
        hub.add(
          const HubEvent('Notification', {
            'notificationId': 'due',
            'type': 'ScheduleDue',
          }),
        );
        await tester.pumpAndSettle();
        expect(find.byType(MaterialBanner), findsNothing);

        // A print ready: a banner, with a way to open it.
        notifications.items = [
          note('done', 'JobCompleted', link: 'neoprint://jobs/j1'),
          ...notifications.items,
        ];
        for (var i = 0; i < 2; i++) {
          hub.add(
            const HubEvent('Notification', {
              'notificationId': 'done',
              'type': 'JobCompleted',
            }),
          );
        }
        await tester.pumpAndSettle();
        expect(find.byType(MaterialBanner), findsOneWidget);
        expect(find.text('Çap hazırdır'), findsOneWidget);

        await tester.tap(find.text('Aç'));
        await tester.pumpAndSettle();
        expect(find.text('home tab'), findsOneWidget);
        expect(notifications.read, ['done']);

        // It goes by itself; the timer must not outlive the test.
        await tester.pump(const Duration(seconds: 7));
      },
    );

    testWidgets('a push that started the app opens its screen and is read', (
      tester,
    ) async {
      final notifications = FakeNotifications()
        ..items = [note('r1', 'RefundCompleted', link: 'neoprint://wallet')];

      await tester.pumpWidget(
        app(
          const NotificationHost(child: Scaffold(body: Text('home'))),
          notifications: notifications,
          platform: FakePushPlatform(
            launch: const PushEvent(
              notificationId: 'r1',
              link: 'neoprint://wallet',
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('wallet'), findsOneWidget);
      expect(notifications.read, ['r1']);
    });

    test(
      'signing out takes this device off; a renewed token is sent again',
      () async {
        final api = FakePushApi();
        final platform = FakePushPlatform();
        final registration = PushRegistration(api, platform);

        await registration.register('ru');
        platform.refreshes.add('fcm-2');
        await Future<void>.delayed(Duration.zero);
        expect(api.registered.map((r) => (r.token, r.language)), [
          ('fcm-1', 'ru'),
          ('fcm-2', 'ru'),
        ]);

        await registration.unregister();
        expect(api.removed, ['fcm-2']);

        // No push on this device: nothing is registered, nothing to take off.
        final none = PushRegistration(api, FakePushPlatform(deviceToken: null));
        await none.register('az');
        await none.unregister();
        expect(api.registered, hasLength(2));
        expect(api.removed, hasLength(1));
      },
    );
  });
}
