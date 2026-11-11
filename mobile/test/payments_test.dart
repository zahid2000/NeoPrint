import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/features/kiosk_search/kiosk_models.dart';
import 'package:neoprint/features/home/near_kiosks.dart';
import 'package:neoprint/l10n/app_localizations.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:go_router/go_router.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:neoprint/core/format/formats.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/realtime/user_hub.dart';
import 'package:neoprint/core/router/app_links.dart';
import 'package:neoprint/features/home/home_screen.dart';
import 'package:neoprint/features/notices/hub_notices.dart';
import 'package:neoprint/features/payments/pay_screen.dart';
import 'package:neoprint/features/payments/payment_models.dart';
import 'package:neoprint/features/wallet/wallet_controller.dart';
import 'package:neoprint/features/wallet/wallet_repository.dart';
import 'package:neoprint/features/wallet/wallet_history_screen.dart';
import 'package:neoprint/features/wallet/wallet_screen.dart';
import 'package:neoprint/l10n/app_localizations_az.dart';

import 'payments_kit.dart';

/// The ledger's history as the server pages it: a period, newest first.
class FakeWalletHistory extends Fake implements WalletRepository {
  FakeWalletHistory(this.entries);

  final List<WalletEntry> entries;
  final asked = <WalletPeriod>[];
  final pages = <int>[];

  @override
  Future<List<WalletEntry>> transactions({
    int page = 1,
    int pageSize = 50,
    DateTime? from,
    DateTime? to,
  }) async {
    if (page == 1) {
      asked.add((from: from!, to: to!));
    }
    pages.add(page);
    final inPeriod = [
      for (final entry in entries)
        if (!entry.createdAt.isBefore(from!) && entry.createdAt.isBefore(to!))
          entry,
    ];
    return inPeriod.skip((page - 1) * pageSize).take(pageSize).toList();
  }
}

void main() {
  test('the amount to pay is never below nothing (T-377)', () {
    final payment = paymentOf(
      amount: 2.4,
      bonusPagesUsable: 25,
      bonusValue: 2.5,
    );

    expect(payment.toPay(useBonus: true), 0);
    expect(payment.toPay(useBonus: false), 2.4);
  });

  group('the money part', () {
    test('bonus pages first, then the promo code on what they leave', () {
      final payment = paymentOf(
        amount: 1.00,
        bonusPagesUsable: 3,
        bonusValue: 0.30,
        promoCode: 'YAZ10',
        promoDiscount: 0.10,
        promoDiscountWithBonus: 0.07,
      );

      expect(payment.toPay(useBonus: false), 0.90);
      expect(payment.toPay(useBonus: true), 0.63);
    });

    test('sums are exact to the qəpik, not floating point', () {
      final payment = paymentOf(
        amount: 0.30,
        bonusPagesUsable: 1,
        bonusValue: 0.10,
      );

      expect(payment.toPay(useBonus: true), 0.20);
    });
  });

  group('the scanned code', () {
    test('only the kiosk app QR is a payment', () {
      final pay = ScannedCode.parse(
        'neoprint://pay/0f8fad5b-d9cb-469f-a165-70867728950e',
      );
      expect(pay, isA<PayCode>());
      expect(
        (pay as PayCode).paymentId,
        '0f8fad5b-d9cb-469f-a165-70867728950e',
      );

      expect(ScannedCode.parse('MOCKBANK:abc'), isA<ForeignCode>());
      expect(ScannedCode.parse('neoprint://pay/../wallet'), isA<ForeignCode>());
      expect(
        ScannedCode.parse('https://neoprint.az/pay/1'),
        isA<ForeignCode>(),
      );
    });

    test('a neoprint://pay link becomes the payment route', () {
      expect(appLinkLocation(Uri.parse('neoprint://pay/abc')), '/pay/abc');
      expect(appLinkLocation(Uri.parse('/home')), isNull);
      expect(appLinkLocation(Uri.parse('neoprint://pay/a/b')), isNull);
    });

    test('the countdown reads like the kiosk', () {
      expect(formatCountdown(272), '4:32');
      expect(formatCountdown(59), '0:59');
      expect(formatCountdown(-3), '0:00');
    });
  });

  group('paying with the app', () {
    testWidgets('shows what is paid for and pays the amount it showed', (
      tester,
    ) async {
      final payments = FakePayments([
        paymentOf(
          amount: 1.00,
          balance: 5,
          bonusPagesUsable: 3,
          bonusValue: 0.30,
        ),
      ])..payAnswer = paymentOf(status: 'Succeeded');

      await tester.pumpWidget(
        step4App(
          PayScreen(paymentId: paymentOf().id),
          overrides: step4Overrides(payments: payments),
        ),
      );
      await tester.pumpAndSettle();

      // With the bonus pages on by default.
      expect(find.text('0,70 ₼'), findsOneWidget);
      expect(find.textContaining('Ödəniş üçün 4:'), findsOneWidget);
      expect(
        find.text('Test kiosk · KSK-9001', findRichText: true),
        findsNothing,
      );
      expect(find.textContaining('KSK-9001'), findsOneWidget);
      expect(find.text('Balansdan'), findsOneWidget);

      // Without them the price is the whole order.
      await tester.ensureVisible(find.byType(Switch));
      await tester.pumpAndSettle();
      await tester.tap(find.byType(Switch));
      await tester.pumpAndSettle();
      expect(find.text('1,00 ₼ ödə'), findsOneWidget);

      await tapText(tester, '1,00 ₼ ödə');
      await tester.pumpAndSettle();

      expect(payments.paid.single, {
        'source': 'Wallet',
        'useBonus': false,
        'expectedAmount': 1.0,
        'key': payments.paid.single['key'],
      });
      expect(find.text('Ödəniş uğurlu'), findsOneWidget);
    });

    testWidgets('a paid order printed now goes on to its own screen (T-378)', (
      tester,
    ) async {
      final payments = FakePayments([paymentOf(amount: 1.00, balance: 5)])
        ..payAnswer = paymentOf(status: 'Succeeded');
      final router = GoRouter(
        routes: [
          GoRoute(
            path: '/',
            builder: (_, __) => PayScreen(paymentId: paymentOf().id),
          ),
          GoRoute(
            path: '/job/:code',
            builder: (_, state) => Text('job ${state.pathParameters['code']}'),
          ),
        ],
      );

      await tester.pumpWidget(
        ProviderScope(
          overrides: step4Overrides(payments: payments),
          child: MaterialApp.router(
            theme: AppTheme.light(),
            locale: const Locale('az'),
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            routerConfig: router,
          ),
        ),
      );
      await tester.pumpAndSettle();

      await tapText(tester, '${formatMoney(1.0)} ödə');
      await tester.pumpAndSettle();

      expect(payments.paid, hasLength(1));
      expect(find.text('job 123456'), findsOneWidget);
    });

    testWidgets('a short balance suggests the card and picks it', (
      tester,
    ) async {
      final payments = FakePayments([paymentOf(amount: 2, balance: 0.5)])
        ..payAnswer = paymentOf(status: 'Succeeded');

      await tester.pumpWidget(
        step4App(
          PayScreen(paymentId: paymentOf().id),
          overrides: step4Overrides(payments: payments),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('Balans çatmır'), findsOneWidget);

      await tapText(tester, '2,00 ₼ ödə');
      await tester.pumpAndSettle();

      expect(payments.paid.single['source'], 'Card');
    });

    testWidgets('a declined card says so and keeps the form', (tester) async {
      final payments = FakePayments([paymentOf(amount: 2, balance: 0)])
        ..payError = const ApiException(
          statusCode: 409,
          message: 'Kart ödənişi qəbul etmədi. Məbləğ tutulmayıb.',
        );

      await tester.pumpWidget(
        step4App(
          PayScreen(paymentId: paymentOf().id),
          overrides: step4Overrides(payments: payments),
        ),
      );
      await tester.pumpAndSettle();

      await tapText(tester, '2,00 ₼ ödə');
      await tester.pumpAndSettle();

      expect(
        find.text('Kart ödənişi qəbul etmədi. Məbləğ tutulmayıb.'),
        findsOneWidget,
      );
      expect(find.text('Ödəniş uğurlu'), findsNothing);

      // A refused attempt is over: the next tap is a new payment request.
      payments.payError = null;
      payments.payAnswer = paymentOf(status: 'Succeeded');
      await tapText(tester, '2,00 ₼ ödə');
      await tester.pumpAndSettle();

      expect(payments.paid, hasLength(2));
      expect(payments.paid.first['key'], isNot(payments.paid.last['key']));
    });

    testWidgets('a lost answer is retried with the same key', (tester) async {
      final payments = FakePayments([paymentOf(amount: 2, balance: 5)])
        ..payError = const ApiException.unreachable();

      await tester.pumpWidget(
        step4App(
          PayScreen(paymentId: paymentOf().id),
          overrides: step4Overrides(payments: payments),
        ),
      );
      await tester.pumpAndSettle();

      await tapText(tester, '2,00 ₼ ödə');
      await tester.pumpAndSettle();
      await tapText(tester, '2,00 ₼ ödə');
      await tester.pumpAndSettle();

      expect(payments.paid, hasLength(2));
      expect(payments.paid.first['key'], payments.paid.last['key']);
    });

    testWidgets('a code typed in the app is checked, shown and paid with', (
      tester,
    ) async {
      final payments = FakePayments([paymentOf(amount: 1.00, balance: 5)])
        ..promos['YAZ10'] = 0.10
        ..payAnswer = paymentOf(status: 'Succeeded');

      await tester.pumpWidget(
        step4App(
          PayScreen(paymentId: paymentOf().id),
          overrides: step4Overrides(payments: payments),
        ),
      );
      await tester.pumpAndSettle();

      await tapText(tester, 'Promokodunuz var?');
      await tester.pumpAndSettle();

      await enterPromo(tester, 'nope');
      await tapText(tester, 'Tətbiq et');
      await tester.pumpAndSettle();
      expect(find.textContaining('Belə promokod yoxdur.'), findsOneWidget);
      expect(find.text('1,00 ₼ ödə'), findsOneWidget);

      await enterPromo(tester, 'yaz10');
      await tapText(tester, 'Tətbiq et');
      await tester.pumpAndSettle();

      expect(payments.previews.last, ('YAZ10', 1.0));
      expect(find.text('Promokod YAZ10: −0,10 ₼'), findsOneWidget);
      expect(find.text('0,90 ₼ ödə'), findsOneWidget);

      await tapText(tester, '0,90 ₼ ödə');
      await tester.pumpAndSettle();

      expect(payments.paid.single['promoCode'], 'YAZ10');
      expect(payments.paid.single['expectedAmount'], 0.9);
    });

    testWidgets('a code the payment refuses is shown on its field', (
      tester,
    ) async {
      final payments = FakePayments([paymentOf(amount: 1.00, balance: 5)])
        ..promos['YAZ10'] = 0.10
        ..payError = const ApiException(
          statusCode: 400,
          message: 'Bu kodun limiti dolub.',
          fieldErrors: {
            'promoCode': ['Bu kodun limiti dolub.'],
          },
        );

      await tester.pumpWidget(
        step4App(
          PayScreen(paymentId: paymentOf().id),
          overrides: step4Overrides(payments: payments),
        ),
      );
      await tester.pumpAndSettle();

      await tapText(tester, 'Promokodunuz var?');
      await tester.pumpAndSettle();
      await enterPromo(tester, 'YAZ10');
      await tapText(tester, 'Tətbiq et');
      await tester.pumpAndSettle();
      await tapText(tester, '0,90 ₼ ödə');
      await tester.pumpAndSettle();

      expect(find.textContaining('Bu kodun limiti dolub.'), findsOneWidget);
      // Back to the full price, as the payment would take it now.
      expect(find.text('1,00 ₼ ödə'), findsOneWidget);
    });

    testWidgets('an order with a kiosk code has no field for another', (
      tester,
    ) async {
      final payments = FakePayments([
        paymentOf(promoCode: 'KIOSK5', promoDiscount: 0.05),
      ]);

      await tester.pumpWidget(
        step4App(
          PayScreen(paymentId: paymentOf().id),
          overrides: step4Overrides(payments: payments),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Promokodunuz var?'), findsNothing);
      expect(find.text('Promokod KIOSK5: −0,05 ₼'), findsOneWidget);
    });

    testWidgets('a promo code that does not apply is said before paying', (
      tester,
    ) async {
      final payments = FakePayments([
        paymentOf(promoCode: 'YAZ10', promoRefusal: 'Kodun müddəti bitib.'),
      ]);

      await tester.pumpWidget(
        step4App(
          PayScreen(paymentId: paymentOf().id),
          overrides: step4Overrides(payments: payments),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('Kodun müddəti bitib.'), findsOneWidget);
    });

    testWidgets('an expired payment says nothing was taken', (tester) async {
      final payments = FakePayments([
        paymentOf(status: 'Expired', secondsLeft: 0),
      ]);

      await tester.pumpWidget(
        step4App(
          PayScreen(paymentId: paymentOf().id),
          overrides: step4Overrides(payments: payments),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Ödəniş vaxtı bitdi'), findsOneWidget);
      expect(find.textContaining('Məbləğ tutulmayıb'), findsOneWidget);
      expect(payments.paid, isEmpty);
    });
  });

  group('home', () {
    testWidgets('as the design draws it: the nearest kiosk offered without '
        'asking, four shortcuts, balance and bonus side by side (T-342)', (
      tester,
    ) async {
      final location = CountingLocation();
      await tester.pumpWidget(
        step4App(
          const HomeScreen(),
          overrides: step4Overrides(
            wallet: walletOf(5, bonusPages: 3),
            location: location,
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Salam,'), findsOneWidget);
      // The four shortcuts, none of them filled: no one main action here.
      expect(find.text('Fayldan çap et'), findsOneWidget);
      expect(find.text('Tərtibat yarat'), findsOneWidget);
      expect(find.text('Fayl paylaş'), findsOneWidget);
      expect(find.byType(FilledButton), findsNothing);
      // The kiosk's QR is read from the bar.
      expect(find.byTooltip('QR oxut'), findsOneWidget);

      // The screen looked without asking; the button is what asks.
      expect(find.text('Yaxın kiosku tapın'), findsOneWidget);
      expect(location.asks, [false]);
      await tester.tap(find.text('Yaxın kioskları göstər'));
      await tester.pumpAndSettle();
      expect(location.asks, [false, true]);

      await tester.scrollUntilVisible(find.text('5,00 ₼'), 200);
      expect(find.text('5,00 ₼'), findsOneWidget);
      expect(find.text('3 səhifə'), findsOneWidget);
      // No order on its way: every order is one row away.
      await tester.scrollUntilVisible(find.text('Sifarişlərim'), 200);
      expect(find.text('Sifarişlərim'), findsOneWidget);
    });

    testWidgets('every kiosk out of service: a warning says none is active '
        '(T-435)', (tester) async {
      await tester.pumpWidget(
        step4App(
          const HomeScreen(),
          overrides: [
            ...step4Overrides(),
            nearKiosksProvider.overrideWith(
              () => _FixedNear(
                const NearKiosks(
                  kiosks: [
                    KioskSummary(
                      id: 'k1',
                      code: 'KSK-1',
                      name: 'Elmlər',
                      address: 'Bakı',
                      lat: 40.4,
                      lng: 49.8,
                      status: KioskStatus.offline,
                      distanceKm: 312,
                    ),
                  ],
                ),
              ),
            ),
          ],
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Hazırda aktiv kiosk yoxdur'), findsOneWidget);
      expect(find.byIcon(Icons.warning_amber_outlined), findsOneWidget);
      expect(find.text('Yenidən yoxla'), findsOneWidget);
      expect(find.text('Yaxın kiosku tapın'), findsNothing);
    });

    testWidgets('without a scanner there is no QR button', (tester) async {
      await tester.pumpWidget(
        step4App(const HomeScreen(), overrides: step4Overrides(canScan: false)),
      );
      await tester.pumpAndSettle();

      expect(find.byTooltip('QR oxut'), findsNothing);
      expect(find.text('Fayldan çap et'), findsOneWidget);
    });

    testWidgets('a job whose result is being checked says the money is safe', (
      tester,
    ) async {
      await tester.pumpWidget(
        step4App(
          const HomeScreen(),
          overrides: step4Overrides(
            jobs: [
              jobOf('Suspected', id: 'a', fileName: 'muqavile.pdf'),
              jobOf('Completed', id: 'b', fileName: 'hesabat.pdf'),
            ],
          ),
        ),
      );
      await tester.pumpAndSettle();

      // The order on its way, under the kiosk and the shortcuts, with the
      // way to every order; the finished one is there, not here.
      await tester.scrollUntilVisible(find.text('Aktiv sifariş'), 200);
      expect(find.text('Hamısına bax'), findsWidgets);
      expect(find.text('Nəticə dəqiqləşdirilir'), findsOneWidget);
      await tester.scrollUntilVisible(
        find.textContaining('Pulunuz itməyib'),
        100,
      );
      expect(find.textContaining('Pulunuz itməyib'), findsOneWidget);
      expect(find.text('Tamamlandı'), findsNothing);
    });
  });

  group('wallet', () {
    testWidgets('the no-refund rule is always on screen; no history is said', (
      tester,
    ) async {
      await tester.pumpWidget(
        step4App(const WalletScreen(), overrides: step4Overrides()),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('geri qaytarılmır'), findsOneWidget);
      expect(find.text('Artır'), findsOneWidget);
      expect(find.text('Hələ hərəkət yoxdur'), findsOneWidget);
    });

    testWidgets('one card holds the balance and the bonus pages; the voucher '
        'code is its own tab (T-355)', (tester) async {
      await tester.pumpWidget(
        step4App(
          const WalletScreen(),
          overrides: step4Overrides(
            wallet: const WalletState(
              wallet: Wallet(balance: 12.4, bonusPages: 25),
              entries: [],
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Cari balans'), findsOneWidget);
      expect(find.text(formatMoney(12.4)), findsOneWidget);
      expect(find.text('Bonus səhifələr'), findsOneWidget);
      expect(find.text('25 səhifə'), findsOneWidget);
      expect(find.text('Əməliyyatlar'), findsOneWidget);

      await tester.tap(find.text('Voucher kodları'));
      await tester.pumpAndSettle();
      expect(find.text('Hələ hərəkət yoxdur'), findsNothing);

      await tester.tap(find.text('Voucher kodunu daxil edin'));
      await tester.pumpAndSettle();
      expect(find.text('Balansa köçür'), findsOneWidget);
    });

    testWidgets('movements show what they were and the balance after', (
      tester,
    ) async {
      await tester.pumpWidget(
        step4App(
          const WalletScreen(),
          overrides: step4Overrides(
            wallet: WalletState(
              wallet: const Wallet(balance: 4.7, bonusPages: 0),
              entries: [
                WalletEntry(
                  id: 'e2',
                  type: 'Payment',
                  amount: -0.3,
                  bonusPages: 0,
                  balanceAfter: 4.7,
                  createdAt: DateTime(2026, 9, 30, 12),
                ),
                WalletEntry(
                  id: 'e1',
                  type: 'TopUp',
                  amount: 5,
                  bonusPages: 0,
                  balanceAfter: 5,
                  createdAt: DateTime(2026, 9, 30, 11),
                ),
              ],
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Ödəniş'), findsOneWidget);
      expect(find.text('−0,30 ₼'), findsOneWidget);
      expect(find.text('+5,00 ₼'), findsOneWidget);
      expect(find.text('Sonra: 4,70 ₼'), findsOneWidget);
    });

    testWidgets(
      'the latest five movements are on the balance screen, every one behind "Hamısına bax" (T-380)',
      (tester) async {
        tester.view
          ..physicalSize = const Size(420, 2400)
          ..devicePixelRatio = 1;
        addTearDown(tester.view.reset);

        final entries = [
          for (var i = 7; i >= 1; i--)
            WalletEntry(
              id: 'e$i',
              type: 'TopUp',
              amount: i.toDouble(),
              bonusPages: 0,
              balanceAfter: i * 10.0,
              createdAt: DateTime(2026, 11, 10, 9, i),
            ),
        ];
        final router = GoRouter(
          routes: [
            GoRoute(path: '/', builder: (_, __) => const WalletScreen()),
            GoRoute(
              path: '/wallet-history',
              builder: (_, __) => const WalletHistoryScreen(),
            ),
          ],
        );
        await tester.pumpWidget(
          ProviderScope(
            overrides: [
              ...step4Overrides(
                wallet: WalletState(
                  wallet: const Wallet(balance: 70, bonusPages: 0),
                  entries: entries,
                ),
              ),
              walletRepositoryProvider.overrideWithValue(
                FakeWalletHistory(entries),
              ),
            ],
            child: MaterialApp.router(
              theme: AppTheme.light(),
              locale: const Locale('az'),
              localizationsDelegates: AppLocalizations.localizationsDelegates,
              supportedLocales: AppLocalizations.supportedLocales,
              routerConfig: router,
            ),
          ),
        );
        await tester.pumpAndSettle();

        expect(find.text('Son əməliyyatlar'), findsOneWidget);
        expect(find.textContaining('Sonra:'), findsNWidgets(5));

        await tester.tap(find.text('Hamısına bax'));
        await tester.pumpAndSettle();

        expect(find.text('Bütün əməliyyatlar'), findsOneWidget);
        expect(find.textContaining('Sonra:'), findsNWidgets(7));
      },
    );

    Future<FakeWalletHistory> history(
      WidgetTester tester,
      List<WalletEntry> entries,
    ) async {
      tester.view
        ..physicalSize = const Size(420, 900)
        ..devicePixelRatio = 1;
      addTearDown(tester.view.reset);

      final server = FakeWalletHistory(entries);
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            ...step4Overrides(),
            walletRepositoryProvider.overrideWithValue(server),
          ],
          child: MaterialApp(
            theme: AppTheme.light(),
            locale: const Locale('az'),
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: const WalletHistoryScreen(),
          ),
        ),
      );
      await tester.pumpAndSettle();
      return server;
    }

    WalletEntry entry(int i, DateTime at) => WalletEntry(
      id: 'e$i',
      type: 'TopUp',
      amount: i.toDouble(),
      bonusPages: 0,
      balanceAfter: i * 10.0,
      createdAt: at,
    );

    testWidgets(
      '"Bütün əməliyyatlar" opens on this month, then the last three months; no "Hamısı" (T-397, T-414)',
      (tester) async {
        final server = await history(tester, [
          entry(2, DateTime(2026, 11, 10, 9)),
          entry(1, DateTime(2026, 9, 20, 9)),
        ]);

        expect(find.text('Hamısı'), findsNothing);
        expect(server.asked.first.from, DateTime(2026, 11));
        expect(find.textContaining('Sonra:'), findsOneWidget);

        await tester.tap(find.text('Son 3 ay'));
        await tester.pumpAndSettle();

        expect(server.asked.last.from, DateTime(2026, 8, 15));
        expect(find.textContaining('Sonra:'), findsNWidgets(2));
      },
    );

    testWidgets(
      'the periods are swiped through too; a swipe never opens the calendar (T-432)',
      (tester) async {
        final server = await history(tester, [
          entry(2, DateTime(2026, 11, 10, 9)),
        ]);

        await tester.fling(
          find.byType(ListView).first,
          const Offset(-300, 0),
          1000,
        );
        await tester.pumpAndSettle();
        expect(server.asked.last.from, DateTime(2026, 8, 15));

        // "Tarix seç" is no step of a swipe until dates were picked.
        await tester.fling(
          find.byType(ListView).first,
          const Offset(-300, 0),
          1000,
        );
        await tester.pumpAndSettle();
        expect(find.byType(DateRangePickerDialog), findsNothing);

        await tester.fling(
          find.byType(ListView).first,
          const Offset(300, 0),
          1000,
        );
        await tester.pumpAndSettle();
        expect(server.asked.last.from, DateTime(2026, 11));
      },
    );

    testWidgets('the history comes twenty at a time as it scrolls (T-413)', (
      tester,
    ) async {
      final server = await history(tester, [
        for (var i = 45; i >= 1; i--)
          entry(i, DateTime(2026, 11, 1).add(Duration(hours: i))),
      ]);

      expect(server.pages, [1], reason: 'one page to open with');

      for (var i = 0; i < 20 && server.pages.length < 3; i++) {
        await tester.drag(find.byType(ListView), const Offset(0, -2000));
        await tester.pumpAndSettle();
      }

      expect(server.pages, [1, 2, 3]);
      await tester.drag(find.byType(ListView), const Offset(0, -5000));
      await tester.pumpAndSettle();
      expect(
        find.textContaining('Sonra: 10,00'),
        findsOneWidget,
        reason: 'the oldest one',
      );
      expect(server.pages, [1, 2, 3], reason: 'nothing past the end');
    });
  });

  group('notices', () {
    final l10n = AppLocalizationsAz();

    test('a job under review is announced; a finished refund is left to its notification banner (T-146)', () {
      expect(
        noticeFor(
          l10n,
          const HubEvent('JobStatusChanged', {'status': 'Suspected'}),
          'az',
        ),
        l10n.noticeSuspected,
      );
      expect(
        noticeFor(
          l10n,
          const HubEvent('RefundStatusChanged', {
            'status': 'Succeeded',
            'amount': 0.3,
            'destination': 'Wallet',
          }),
          'az',
        ),
        isNull,
      );
    });

    test('everything else only refreshes the lists', () {
      expect(
        noticeFor(
          l10n,
          const HubEvent('JobStatusChanged', {'status': 'Queued'}),
          'az',
        ),
        isNull,
      );
      expect(
        noticeFor(
          l10n,
          const HubEvent('RefundStatusChanged', {'status': 'Processing'}),
          'az',
        ),
        isNull,
      );
    });
  });
}

/// Taps the widget with [text] after scrolling it into view: the pay screen
/// is a list, and the test window is small.
Future<void> tapText(WidgetTester tester, String text) async {
  final finder = find.text(text);
  await tester.ensureVisible(finder);
  await tester.pumpAndSettle();
  await tester.tap(finder);
}

Future<void> enterPromo(WidgetTester tester, String code) async {
  final field = find.byType(TextField);
  await tester.ensureVisible(field);
  await tester.pumpAndSettle();
  await tester.enterText(field, code);
}

class _FixedNear extends NearKiosksController {
  _FixedNear(this.value);

  final NearKiosks value;

  @override
  Future<NearKiosks> build() async => value;
}
