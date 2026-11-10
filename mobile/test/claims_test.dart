import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/jobs/claims_screen.dart';
import 'package:neoprint/features/jobs/jobs_controller.dart';
import 'package:neoprint/features/jobs/jobs_repository.dart';
import 'package:neoprint/l10n/app_localizations.dart';

/// Claims the test sets; "Müraciəti davam etdir" opens one again.
class FakeClaims implements JobsRepository {
  FakeClaims(this.list);

  List<MyClaim> list;
  final reopened = <String>[];

  @override
  Future<List<MyClaim>> claims({int? page, int? pageSize}) async =>
      (page ?? 1) == 1 ? list : const [];

  @override
  Future<void> reopenClaim(
    String claimId, {
    required String idempotencyKey,
  }) async {
    reopened.add(claimId);
    list = [
      for (final claim in list)
        claim.id == claimId
            ? MyClaim(
                id: claim.id,
                jobCode: claim.jobCode,
                text: claim.text,
                status: 'Open',
                reply: claim.reply,
                createdAt: claim.createdAt,
              )
            : claim,
    ];
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

Widget app(FakeClaims claims) => ProviderScope(
  overrides: [jobsRepositoryProvider.overrideWithValue(claims)],
  child: MaterialApp(
    theme: AppTheme.light(),
    locale: const Locale('az'),
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: const ClaimsScreen(),
  ),
);

void main() {
  // T-385: an answer closes the claim for the customer, who may carry it on.
  testWidgets('an answered claim can be carried on; an open or closed one '
      'cannot', (tester) async {
    final claims = FakeClaims([
      MyClaim(
        id: 'c1',
        jobCode: '482913',
        text: 'Çap getmir',
        status: 'Answered',
        reply: 'Kioskda yoxladıq, sifarişi ləğv etdik.',
        createdAt: DateTime(2026, 11, 10, 9),
      ),
      MyClaim(
        id: 'c2',
        jobCode: '111111',
        text: 'Kağız çıxmadı',
        status: 'Closed',
        createdAt: DateTime(2026, 11, 9, 9),
      ),
    ]);
    await tester.pumpWidget(app(claims));
    await tester.pumpAndSettle();

    expect(find.text('Kioskda yoxladıq, sifarişi ləğv etdik.'), findsOneWidget);
    expect(find.text('Müraciəti davam etdir'), findsOneWidget);

    await tester.tap(find.text('Müraciəti davam etdir'));
    await tester.pumpAndSettle();

    expect(claims.reopened, ['c1']);
    expect(
      find.text('Müraciət yenidən açıldı — admin baxacaq.'),
      findsOneWidget,
    );
    // Open now: nothing more to carry on.
    expect(find.text('Müraciəti davam etdir'), findsNothing);
    expect(find.text('Baxılır'), findsOneWidget);
  });
}
