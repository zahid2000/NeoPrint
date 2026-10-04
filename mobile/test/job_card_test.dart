import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/features/jobs/job_card.dart';
import 'package:neoprint/features/jobs/jobs_repository.dart';

import 'payments_kit.dart';

MyJob job({DateTime? scheduledAt, String status = 'Paid'}) => MyJob(
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
  });
}
