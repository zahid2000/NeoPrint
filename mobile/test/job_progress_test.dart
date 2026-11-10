import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/jobs/job_progress_screen.dart';
import 'package:neoprint/features/jobs/jobs_controller.dart';
import 'package:neoprint/features/jobs/jobs_repository.dart';
import 'package:neoprint/l10n/app_localizations.dart';

import 'job_card_test.dart' show CancellingJobs;

/// The order's own screen after it was paid (T-378): its stage, and
/// "Pəncərəni bağla", "Sifarişi ləğv et", "Yenidən çap et".
class ShownJobs extends CancellingJobs {
  ShownJobs(this.jobs);

  final List<MyJob> jobs;

  @override
  Future<List<MyJob>> build() async => jobs;
}

MyJob order(
  String status, {
  int printedSides = 0,
  bool canCancel = false,
  String? fileId = 'f1',
}) => MyJob(
  id: 'j1',
  jobCode: '482913',
  status: status,
  kioskCode: 'KSK-9002',
  kioskName: 'Elmlər',
  fileName: 'hesabat.pdf',
  pages: 6,
  copies: 2,
  color: false,
  paper: 'A4',
  amount: 1.20,
  createdAt: DateTime(2026, 11, 10, 9),
  canCancel: canCancel,
  printedSides: printedSides,
  fileId: fileId,
);

Future<ShownJobs> show(WidgetTester tester, MyJob job) async {
  tester.view
    ..physicalSize = const Size(420, 1400)
    ..devicePixelRatio = 1;
  addTearDown(tester.view.reset);

  final jobs = ShownJobs([job]);
  final router = GoRouter(
    routes: [
      GoRoute(
        path: '/',
        builder: (_, __) => const JobProgressScreen(jobCode: '482913'),
      ),
      GoRoute(path: '/home', builder: (_, __) => const Text('home')),
      GoRoute(
        path: '/file/:id',
        builder: (_, state) => Text('file ${state.pathParameters['id']}'),
      ),
    ],
  );

  await tester.pumpWidget(
    ProviderScope(
      overrides: [myJobsProvider.overrideWith(() => jobs)],
      child: MaterialApp.router(
        theme: AppTheme.light(),
        locale: const Locale('az'),
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        routerConfig: router,
      ),
    ),
  );
  await tester.pump();
  await tester.pump();
  return jobs;
}

/// A spinner turns while it waits: time is let pass, not settled.
Future<void> settle(WidgetTester tester) async {
  for (var i = 0; i < 10; i++) {
    await tester.pump(const Duration(milliseconds: 100));
  }
}

void main() {
  testWidgets('in the queue: closing it leaves it printing; it can be '
      'cancelled while it waits', (tester) async {
    final jobs = await show(tester, order('Queued', canCancel: true));

    expect(
      find.textContaining('Elmlər kioskunun növbəsindədir'),
      findsOneWidget,
    );
    expect(find.text('Sifariş qəbul edildi'), findsOneWidget);
    expect(find.text('Ödəniş təsdiqləndi'), findsOneWidget);
    expect(find.text('Yenidən çap et'), findsNothing);

    await tester.tap(find.text('Sifarişi ləğv et'));
    await settle(tester);
    await tester.tap(find.text('Bəli, ləğv et'));
    await settle(tester);
    expect(jobs.cancelled, ['j1']);

    await tester.tap(find.text('Pəncərəni bağla'));
    await settle(tester);
    expect(find.text('home'), findsOneWidget);
  });

  testWidgets('printing: the pages so far of all of them', (tester) async {
    await show(tester, order('Printing', printedSides: 5));

    expect(find.text('Çap olunur…'), findsOneWidget);
    // 6 pages × 2 copies.
    expect(find.text('5 / 12 səhifə'), findsOneWidget);
    expect(find.text('Sifarişi ləğv et'), findsNothing);
    // Not the owner's to cancel, but theirs to report: an admin looks (T-385).
    expect(find.text('Problem barədə bildir'), findsWidgets);
    expect(find.textContaining('Çap getmir?'), findsOneWidget);
    // The share in the ring and beside the card's bar (T-377).
    expect(find.text('41%'), findsNWidgets(2));
    // "Çap olunur" is the step under way, not one behind it.
    final printingStep = find.ancestor(
      of: find.text('Çap olunur'),
      matching: find.byType(ListTile),
    );
    expect(
      find.descendant(
        of: printingStep,
        matching: find.byType(CircularProgressIndicator),
      ),
      findsOneWidget,
    );
  });

  testWidgets('before the first page the ring shows the printer, no share', (
    tester,
  ) async {
    await show(tester, order('Printing'));

    expect(find.byIcon(Icons.print_outlined), findsWidgets);
    expect(find.textContaining('%'), findsNothing);
  });

  testWidgets('printed: "Yenidən çap et" opens the same file', (tester) async {
    await show(tester, order('Completed', printedSides: 12));

    expect(find.text('Çap tamamlandı!'), findsOneWidget);
    await tester.tap(find.text('Yenidən çap et'));
    await tester.pumpAndSettle();
    expect(find.text('file f1'), findsOneWidget);
  });

  testWidgets('an order made from a flash drive cannot be printed again '
      'from the app', (tester) async {
    await show(tester, order('Completed', fileId: null));

    expect(find.text('Çap tamamlandı!'), findsOneWidget);
    expect(find.text('Yenidən çap et'), findsNothing);
    expect(find.text('Pəncərəni bağla'), findsOneWidget);
  });
}
