import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/realtime/user_hub.dart';
import 'package:neoprint/core/router/app_links.dart';
import 'package:neoprint/features/home/home_screen.dart';
import 'package:neoprint/features/notices/hub_notices.dart';
import 'package:neoprint/features/payments/pay_screen.dart';
import 'package:neoprint/features/payments/payment_models.dart';
import 'package:neoprint/features/wallet/wallet_controller.dart';
import 'package:neoprint/features/wallet/wallet_repository.dart';
import 'package:neoprint/features/wallet/wallet_screen.dart';
import 'package:neoprint/l10n/app_localizations_az.dart';

import 'payments_kit.dart';

void main() {
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
    testWidgets('balance first, scanning as the one primary action', (
      tester,
    ) async {
      await tester.pumpWidget(
        step4App(
          const HomeScreen(),
          overrides: step4Overrides(wallet: walletOf(5, bonusPages: 3)),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('5,00 ₼'), findsOneWidget);
      expect(find.text('3 bonus səhifə'), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'QR oxut'), findsOneWidget);
      expect(find.widgetWithText(OutlinedButton, 'Fayllarım'), findsOneWidget);
      expect(find.text('Hələ sifarişiniz yoxdur'), findsOneWidget);
    });

    testWidgets('without a scanner the files stay the primary action', (
      tester,
    ) async {
      await tester.pumpWidget(
        step4App(const HomeScreen(), overrides: step4Overrides(canScan: false)),
      );
      await tester.pumpAndSettle();

      expect(find.text('QR oxut'), findsNothing);
      expect(find.widgetWithText(FilledButton, 'Fayllarım'), findsOneWidget);
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

      expect(find.text('Aktiv çaplar'), findsOneWidget);
      expect(find.text('Son sifarişlər'), findsOneWidget);
      expect(find.text('Nəticə dəqiqləşdirilir'), findsOneWidget);
      expect(find.textContaining('Pulunuz itməyib'), findsOneWidget);
      expect(find.text('Tamamlandı'), findsOneWidget);
      expect(find.text('Problem barədə bildir'), findsNWidgets(2));
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
      expect(find.text('Balansı artır'), findsOneWidget);
      expect(find.text('Hələ hərəkət yoxdur'), findsOneWidget);
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
