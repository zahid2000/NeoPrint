import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/realtime/user_hub.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/kiosk_search/kiosk_search_controller.dart';
import 'package:neoprint/features/schedule/schedule_create_screen.dart';
import 'package:neoprint/features/schedule/schedule_due_prompt.dart';
import 'package:neoprint/features/schedule/schedule_models.dart';
import 'package:neoprint/features/schedule/schedule_repository.dart';
import 'package:neoprint/features/schedule/schedule_screen.dart';
import 'package:neoprint/features/session/session_controller.dart';
import 'package:neoprint/features/session/session_models.dart';
import 'package:neoprint/l10n/app_localizations.dart';

import 'kiosk_search_test.dart' show FakeKiosks, elmler;
import 'prepared_test.dart' show file, money, tallScreen;
import 'session_test.dart' show FakeSessions, detail, hubEvents;

/// The schedules API the test scripts.
class FakeSchedules implements ScheduleRepository {
  List<ScheduledPrint> items = [];
  final created = <({String kiosk, DateTime at, double expected})>[];
  final printed = <String>[];
  final moved = <String>[];
  final cancelled = <String>[];
  ApiException? printError;

  @override
  Future<List<ScheduledPrint>> list() async => items;

  @override
  Future<PlacedSchedule> create({
    required String kiosk,
    required String fileId,
    required OrderOptions options,
    required DateTime scheduledAt,
    required double expectedAmount,
    required String idempotencyKey,
  }) async {
    created.add((kiosk: kiosk, at: scheduledAt, expected: expectedAmount));
    return const PlacedSchedule(
      scheduleId: 's1',
      paymentId: 'pay1',
      amount: 0.30,
    );
  }

  @override
  Future<ScheduledPrint> reschedule(String id, DateTime scheduledAt) async {
    moved.add(id);
    return items.firstWhere((s) => s.id == id);
  }

  @override
  Future<ScheduledPrint> printNow(String id) async {
    if (printError case final error?) {
      throw error;
    }

    printed.add(id);
    return items.firstWhere((s) => s.id == id);
  }

  @override
  Future<void> cancel(String id) async {
    cancelled.add(id);
    items = [
      for (final s in items)
        if (s.id != id) s,
    ];
  }
}

ScheduledPrint schedule(
  String id,
  ScheduleStatus status, {
  String jobStatus = 'Paid',
  DateTime? autoCancelAt,
}) => ScheduledPrint(
  id: id,
  jobId: 'j-$id',
  jobCode: '654321',
  fileId: 'f1',
  fileName: 'bilet-$id.pdf',
  pages: 3,
  options: const OrderOptions(),
  kioskId: 'k1',
  kioskCode: 'KSK-9002',
  kioskName: 'Elmlər',
  amount: 0.30,
  // Not the suite's day (flutter_test_config.dart) nor the one before it:
  // those two read as words.
  scheduledAt: DateTime(2030, 3, 14, 9),
  status: status,
  jobStatus: jobStatus,
  paymentId: 'pay-$id',
  autoCancelAt: autoCancelAt,
);

Widget app(Widget home, FakeSchedules schedules) => ProviderScope(
  overrides: [
    scheduleRepositoryProvider.overrideWithValue(schedules),
    sessionRepositoryProvider.overrideWithValue(FakeSessions()),
    kioskRepositoryProvider.overrideWithValue(
      FakeKiosks([elmler], details: {elmler.id: detail}),
    ),
    userHubEventsProvider.overrideWithValue(hubEvents.stream),
    userHubReconnectedProvider.overrideWithValue(const Stream.empty()),
  ],
  child: MaterialApp.router(
    theme: AppTheme.light(),
    locale: const Locale('az'),
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    routerConfig: GoRouter(
      routes: [
        GoRoute(path: '/', builder: (_, __) => home),
        GoRoute(
          path: '/pay/:id',
          builder: (_, state) => Text('pay ${state.pathParameters['id']}'),
        ),
        GoRoute(path: '/schedule/new', builder: (_, __) => const Text('new')),
      ],
    ),
  ),
);

/// The OK button of the open date or time picker, in the app's language.
Finder ok(WidgetTester tester) => find.text(
  MaterialLocalizations.of(tester.element(find.byType(Dialog).last))
      .okButtonLabel,
);

void main() {
  group('scheduled prints tab', () {
    testWidgets('an empty list says what scheduling is for', (tester) async {
      await tester.pumpWidget(app(const ScheduleScreen(), FakeSchedules()));
      await tester.pumpAndSettle();

      expect(find.text('Planlanmış çapınız yoxdur'), findsOneWidget);
      await tester.tap(find.text('Yeni planlama'));
      await tester.pumpAndSettle();
      expect(find.text('new'), findsOneWidget);
    });

    testWidgets(
      'each status has its words, and a due one asks to print or move it',
      (tester) async {
        tallScreen(tester);
        final schedules = FakeSchedules()
          ..items = [
            schedule(
              'due',
              ScheduleStatus.awaitingUser,
              autoCancelAt: DateTime(2030, 3, 15, 9),
            ),
            schedule('later', ScheduleStatus.scheduled),
            schedule('done', ScheduleStatus.sent, jobStatus: 'Completed'),
            schedule('gone', ScheduleStatus.expired, jobStatus: 'Cancelled'),
          ];
        await tester.pumpWidget(app(const ScheduleScreen(), schedules));
        await tester.pumpAndSettle();

        expect(find.text('Vaxtı çatıb'), findsOneWidget);
        expect(find.text('Planlanıb'), findsOneWidget);
        expect(find.text('Çap olunub'), findsOneWidget);
        expect(find.text('Vaxtı keçdi'), findsOneWidget);
        expect(find.text('Pul balansa qaytarıldı.'), findsOneWidget);
        expect(find.text('KSK-9002 · 14.03.2030 09:00'), findsNWidgets(4));
        expect(
          find.text(
            '15.03.2030 09:00-dək cavab verməsəniz ləğv olunacaq, pul balansa qayıdacaq.',
          ),
          findsOneWidget,
        );
        // Closed ones have nothing left to do.
        expect(find.byType(PopupMenuButton<String>), findsNWidgets(2));

        await tester.tap(find.text('Çapa ver'));
        await tester.pumpAndSettle();
        expect(schedules.printed, ['due']);
        expect(
          find.text('Çapa verildi. KSK-9002 kioskunda sifariş kodu: 654321'),
          findsOneWidget,
        );
      },
    );

    testWidgets(
      'a kiosk that cannot print now says why and offers another time',
      (tester) async {
        tallScreen(tester);
        final schedules = FakeSchedules()
          ..items = [schedule('due', ScheduleStatus.awaitingUser)]
          ..printError = const ApiException(
            statusCode: 409,
            message: 'KSK-9002 kioskunda hazırda çap etmək olmur (Paused).',
          );
        await tester.pumpWidget(app(const ScheduleScreen(), schedules));
        await tester.pumpAndSettle();

        await tester.tap(find.text('Çapa ver'));
        await tester.pumpAndSettle();

        expect(find.text('Çapa vermək olmadı'), findsOneWidget);
        expect(find.textContaining('(Paused).'), findsOneWidget);
        expect(
          find.widgetWithText(FilledButton, 'Vaxtı dəyiş'),
          findsOneWidget,
        );
        expect(schedules.printed, isEmpty);
      },
    );

    testWidgets('cancelling says what comes back and takes it off the list', (
      tester,
    ) async {
      tallScreen(tester);
      final schedules = FakeSchedules()
        ..items = [schedule('later', ScheduleStatus.scheduled)];
      await tester.pumpWidget(app(const ScheduleScreen(), schedules));
      await tester.pumpAndSettle();

      await tester.tap(find.byType(PopupMenuButton<String>));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Ləğv et').last);
      await tester.pumpAndSettle();
      expect(
        find.text('${money(0.30)} balansınıza qaytarılacaq.'),
        findsOneWidget,
      );
      await tester.tap(find.text('Ləğv et').last);
      await tester.pumpAndSettle();

      expect(schedules.cancelled, ['later']);
      expect(find.text('Planlanmış çapınız yoxdur'), findsOneWidget);
    });

    testWidgets('an unpaid one can still be paid from its menu', (
      tester,
    ) async {
      final schedules = FakeSchedules()
        ..items = [schedule('new', ScheduleStatus.pendingPayment)];
      await tester.pumpWidget(app(const ScheduleScreen(), schedules));
      await tester.pumpAndSettle();

      expect(find.text('Ödəniş gözlənilir'), findsOneWidget);
      await tester.tap(find.byType(PopupMenuButton<String>));
      await tester.pumpAndSettle();
      expect(find.text('Çapa ver'), findsNothing);
      await tester.tap(find.text('Ödə'));
      await tester.pumpAndSettle();
      expect(find.text('pay pay-new'), findsOneWidget);
    });
  });

  group('schedule a print', () {
    testWidgets(
      'says what is missing, then prices it at the kiosk and pays for the chosen time',
      (tester) async {
        tallScreen(tester);
        final schedules = FakeSchedules();
        await tester.pumpWidget(
          app(ScheduleCreateScreen(file: file), schedules),
        );
        await tester.pumpAndSettle();

        expect(find.text('hesabat.pdf'), findsOneWidget);
        await tester.tap(find.text('Ödə və planla'));
        await tester.pumpAndSettle();
        expect(find.text('Əvvəl kiosk seçin.'), findsOneWidget);

        await tester.tap(find.text('Kiosk seç'));
        await tester.pumpAndSettle();
        await tester.tap(find.text('KSK-9002 · Elmlər'));
        await tester.pumpAndSettle();
        await tester.pump(const Duration(milliseconds: 400));
        await tester.pumpAndSettle();

        expect(find.text('Bu kiosk rəngli çap etmir.'), findsOneWidget);
        await tester.tap(find.text('Ödə və planla — ${money(0.30)}'));
        await tester.pumpAndSettle();
        expect(find.text('Əvvəl tarix və saat seçin.'), findsOneWidget);
        expect(schedules.created, isEmpty);

        // The pickers start an hour and a quarter from now: taken as offered.
        final before = DateTime.now();
        await tester.tap(find.text('Tarix və saat seç'));
        await tester.pumpAndSettle();
        await tester.tap(ok(tester));
        await tester.pumpAndSettle();
        await tester.tap(ok(tester));
        await tester.pumpAndSettle();

        await tester.tap(find.text('Ödə və planla — ${money(0.30)}'));
        await tester.pumpAndSettle();

        final made = schedules.created.single;
        expect(made.kiosk, elmler.id);
        expect(made.expected, 0.30);
        expect(
          made.at.difference(before),
          greaterThanOrEqualTo(const Duration(minutes: 15)),
        );
        expect(find.text('pay pay1'), findsOneWidget);
      },
    );
  });

  testWidgets(
    'when the time comes the app asks, wherever the user is, and prints',
    (tester) async {
      final schedules = FakeSchedules()
        ..items = [schedule('due', ScheduleStatus.awaitingUser)];
      await tester.pumpWidget(
        app(
          const ScheduleDuePrompt(child: Scaffold(body: Text('home'))),
          schedules,
        ),
      );
      await tester.pumpAndSettle();

      hubEvents.add(
        const HubEvent('ScheduleDue', {
          'scheduleId': 'due',
          'status': 'AwaitingUser',
        }),
      );
      await tester.pumpAndSettle();

      expect(find.text('Planlanmış çapın vaxtı çatıb'), findsOneWidget);
      expect(
        find.text(
          'bilet-due.pdf · KSK-9002 kioskunda. Kioskun yanındasınızsa çapa verin.',
        ),
        findsOneWidget,
      );

      await tester.tap(find.text('Çapa ver'));
      await tester.pumpAndSettle();
      expect(schedules.printed, ['due']);
    },
  );

  testWidgets(
    'two due at the same minute share one dialog, each with its own buttons (T-159)',
    (tester) async {
      final schedules = FakeSchedules()
        ..items = [
          schedule('a', ScheduleStatus.awaitingUser),
          schedule('b', ScheduleStatus.awaitingUser),
        ];
      await tester.pumpWidget(
        app(
          const ScheduleDuePrompt(child: Scaffold(body: Text('home'))),
          schedules,
        ),
      );
      await tester.pumpAndSettle();

      for (final id in ['a', 'b']) {
        hubEvents.add(
          HubEvent('ScheduleDue', {'scheduleId': id, 'status': 'AwaitingUser'}),
        );
      }
      await tester.pumpAndSettle();

      // One dialog, not two stacked.
      expect(find.byType(AlertDialog), findsOneWidget);
      expect(find.text('Planlanmış çapların vaxtı çatıb'), findsOneWidget);
      expect(find.text('bilet-a.pdf'), findsOneWidget);
      expect(find.text('bilet-b.pdf'), findsOneWidget);
      expect(find.text('Çapa ver'), findsNWidgets(2));

      // The kiosk refuses the first: why, under that row; the dialog stays.
      schedules.printError = const ApiException(
        statusCode: 409,
        message: 'KSK-9002 kioskunda kağız qurtarıb.',
      );
      await tester.tap(find.text('Çapa ver').first);
      await tester.pumpAndSettle();
      expect(
        find.textContaining('KSK-9002 kioskunda kağız qurtarıb.'),
        findsOneWidget,
      );
      expect(find.text('bilet-a.pdf'), findsOneWidget);

      // Sent: it leaves the list; the last one is asked as a single.
      schedules.printError = null;
      await tester.tap(find.text('Çapa ver').first);
      await tester.pumpAndSettle();
      expect(schedules.printed, ['a']);
      expect(find.text('Planlanmış çapın vaxtı çatıb'), findsOneWidget);
      expect(
        find.text(
          'bilet-b.pdf · KSK-9002 kioskunda. Kioskun yanındasınızsa çapa verin.',
        ),
        findsOneWidget,
      );

      await tester.tap(find.text('Çapa ver'));
      await tester.pumpAndSettle();
      expect(schedules.printed, ['a', 'b']);
      expect(find.byType(AlertDialog), findsNothing);
    },
  );
}
