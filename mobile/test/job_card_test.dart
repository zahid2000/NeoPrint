import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/features/jobs/job_card.dart';
import 'package:neoprint/features/jobs/jobs_controller.dart';
import 'package:neoprint/features/jobs/jobs_repository.dart';

import 'payments_kit.dart';

/// The jobs controller as the card uses it: what was cancelled, and what
/// the backend answered.
class CancellingJobs extends MyJobsController {
  CancellingJobs({this.refusal});

  final ApiException? refusal;
  final cancelled = <String>[];

  @override
  Future<List<MyJob>> build() async => const [];

  @override
  Future<ApiException?> cancel(String jobId) async {
    cancelled.add(jobId);
    return refusal;
  }
}

MyJob job({
  DateTime? scheduledAt,
  String status = 'Paid',
  bool canCancel = false,
  bool kioskStopped = false,
}) => MyJob(
  id: 'j1',
  jobCode: '654321',
  status: status,
  kioskCode: 'KSK-9002',
  fileName: 'bilet.pdf',
  pages: 1,
  copies: 1,
  color: false,
  paper: 'A4',
  amount: 0.20,
  createdAt: DateTime(2026, 10, 4, 9),
  scheduledAt: scheduledAt,
  canCancel: canCancel,
  kioskStopped: kioskStopped,
);

void main() {
  testWidgets('a scheduled print waiting for its time says when, not "paid" '
      '(T-158)', (tester) async {
    await tester.pumpWidget(
      step4App(JobCard(job: job(scheduledAt: DateTime(2026, 10, 6, 9, 30)))),
    );

    expect(find.text('Planlanıb · 06.10.2026 09:30'), findsOneWidget);
    expect(find.text('Ödənildi'), findsNothing);
  });

  testWidgets('a paid order at the kiosk is still "paid"', (tester) async {
    await tester.pumpWidget(step4App(JobCard(job: job())));

    expect(find.text('Ödənildi'), findsOneWidget);
    expect(find.text('Sifarişi ləğv et'), findsNothing);
    expect(find.text('Ləğv et və məbləği qaytar'), findsNothing);
  });

  group('a kiosk that stopped (plan section 9.6)', () {
    Future<CancellingJobs> show(
      WidgetTester tester,
      MyJob shown, {
      ApiException? refusal,
    }) async {
      final jobs = CancellingJobs(refusal: refusal);

      await tester.pumpWidget(
        step4App(
          Scaffold(
            body: SingleChildScrollView(child: JobCard(job: shown)),
          ),
          overrides: [myJobsProvider.overrideWith(() => jobs)],
        ),
      );

      return jobs;
    }

    testWidgets('an order waiting there is offered: wait, or cancel and take '
        'the money back', (tester) async {
      final jobs = await show(
        tester,
        job(status: 'Queued', canCancel: true, kioskStopped: true),
      );

      expect(find.textContaining('Kioskda printer işləmir'), findsOneWidget);

      await tester.tap(find.text('Ləğv et və məbləği qaytar'));
      await tester.pumpAndSettle();

      // Asked once more, with what comes back and where.
      expect(find.text('Sifariş ləğv edilsin?'), findsOneWidget);
      expect(
        // The amount and its sign are kept together by a no-break space.
        find.textContaining('0,20 ₼ NeoPrint balansınıza qaytarılacaq'),
        findsOneWidget,
      );
      expect(jobs.cancelled, isEmpty, reason: 'not before the answer');

      await tester.tap(find.text('Bəli, ləğv et'));
      await tester.pumpAndSettle();

      expect(jobs.cancelled, ['j1']);
      expect(find.textContaining('Sifariş ləğv edildi'), findsOneWidget);
    });

    testWidgets('"no, I will wait" cancels nothing', (tester) async {
      final jobs = await show(
        tester,
        job(status: 'Queued', canCancel: true, kioskStopped: true),
      );

      await tester.tap(find.text('Ləğv et və məbləği qaytar'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Xeyr, gözləyirəm'));
      await tester.pumpAndSettle();

      expect(jobs.cancelled, isEmpty);
      expect(find.text('Sifariş ləğv edilsin?'), findsNothing);
    });

    testWidgets('an order the printer stopped says so and offers the same', (
      tester,
    ) async {
      await show(tester, job(status: 'Interrupted', canCancel: true));

      expect(find.text('Dayandı'), findsOneWidget);
      expect(find.textContaining('Çap yarımçıq qaldı'), findsOneWidget);
      expect(find.text('Ləğv et və məbləği qaytar'), findsOneWidget);
    });

    testWidgets('a refusal is said in words and the card stays', (
      tester,
    ) async {
      final jobs = await show(
        tester,
        job(status: 'Interrupted', canCancel: true),
        refusal: const ApiException(
          statusCode: 409,
          message: 'Sifariş artıq çap olunur, onu ləğv etmək olmaz.',
          code: 'printJobs.cancelWhilePrinting',
        ),
      );

      await tester.tap(find.text('Ləğv et və məbləği qaytar'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Bəli, ləğv et'));
      await tester.pumpAndSettle();

      expect(jobs.cancelled, ['j1']);
      expect(
        find.text('Sifariş artıq çap olunur, onu ləğv etmək olmaz.'),
        findsOneWidget,
      );
      expect(find.textContaining('Sifariş ləğv edildi'), findsNothing);
    });
  });

  testWidgets('an order waiting at a working kiosk can still be cancelled, '
      'quietly', (tester) async {
    await tester.pumpWidget(
      step4App(JobCard(job: job(status: 'Queued', canCancel: true))),
    );

    expect(find.text('Sifarişi ləğv et'), findsOneWidget);
    expect(find.textContaining('Kioskda printer işləmir'), findsNothing);
    expect(find.text('Ləğv et və məbləği qaytar'), findsNothing);
  });

  test('a stopped order is still on its way: it is among the active ones', () {
    expect(job(status: 'Interrupted').isActive, isTrue);
    expect(job(status: 'Cancelled').isActive, isFalse);
  });

  test('the server says what may be cancelled; an old server says nothing', () {
    final parsed = MyJob.fromJson({
      'id': 'j1',
      'jobCode': '654321',
      'status': 'Queued',
      'createdAt': '2026-10-04T09:00:00Z',
      'canCancel': true,
      'kioskStopped': true,
    });
    expect(parsed.offersRefund, isTrue);

    final old = MyJob.fromJson({
      'id': 'j1',
      'jobCode': '654321',
      'status': 'Interrupted',
      'createdAt': '2026-10-04T09:00:00Z',
    });
    expect(old.canCancel, isFalse);
    expect(old.offersRefund, isFalse);
  });
}
