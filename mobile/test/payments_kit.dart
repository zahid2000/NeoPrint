import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:neoprint/core/auth/auth_controller.dart';
import 'package:neoprint/core/auth/auth_models.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/realtime/user_hub_provider.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/jobs/jobs_controller.dart';
import 'package:neoprint/features/jobs/jobs_repository.dart';
import 'package:neoprint/features/payments/pay_screen.dart';
import 'package:neoprint/features/payments/payment_models.dart';
import 'package:neoprint/features/payments/payments_repository.dart';
import 'package:neoprint/features/payments/scan_screen.dart';
import 'package:neoprint/features/wallet/wallet_controller.dart';
import 'package:neoprint/features/wallet/wallet_repository.dart';
import 'package:neoprint/l10n/app_localizations.dart';

/// Test doubles for the step 4 screens: no network, no hub.

const testUser = UserProfile(
  id: 'u1',
  publicId: 'NP-ABC123',
  email: 'dev-user@neoprint.local',
  language: 'az',
  emailConfirmed: true,
);

class FakeWallet extends WalletController {
  FakeWallet(this.state0);

  final WalletState state0;

  @override
  Future<WalletState> build() async => state0;
}

class FakeJobs extends MyJobsController {
  FakeJobs(this.jobs);

  final List<MyJob> jobs;

  @override
  Future<List<MyJob>> build() async => jobs;
}

/// A payments API the test scripts: [get] answers in turn, [pay] records
/// what was sent and answers with [payAnswer] or throws [payError].
class FakePayments implements PaymentsRepository {
  FakePayments(this.views);

  final List<AppPayment> views;
  AppPayment? payAnswer;
  ApiException? payError;

  /// Codes the preview accepts, with what they take off.
  final promos = <String, double>{};
  final previews = <(String, double)>[];
  final paid = <Map<String, Object>>[];

  @override
  Future<AppPayment> get(String paymentId) async =>
      views.length > 1 ? views.removeAt(0) : views.single;

  @override
  Future<AppPayment> pay(
    String paymentId, {
    required PaymentSource source,
    required bool useBonus,
    required double expectedAmount,
    required String idempotencyKey,
    String? promoCode,
  }) async {
    paid.add({
      'source': source.wire,
      'useBonus': useBonus,
      'expectedAmount': expectedAmount,
      'key': idempotencyKey,
      'promoCode': ?promoCode,
    });

    final error = payError;
    if (error != null) {
      throw error;
    }

    return payAnswer!;
  }

  @override
  Future<PromoPreview> previewPromo(String code, double amount) async {
    previews.add((code, amount));
    final discount = promos[code];

    return PromoPreview(
      code: code,
      valid: discount != null,
      discount: discount ?? 0,
      reason: discount == null ? 'Belə promokod yoxdur.' : null,
    );
  }
}

WalletState walletOf(double balance, {int bonusPages = 0}) => WalletState(
  wallet: Wallet(balance: balance, bonusPages: bonusPages),
  entries: const [],
);

MyJob jobOf(String status, {String id = 'j1', String? fileName}) => MyJob(
  id: id,
  jobCode: '123456',
  status: status,
  kioskCode: 'KSK-9001',
  kioskName: 'Test kiosk',
  fileName: fileName ?? 'hesabat.pdf',
  pages: 3,
  copies: 1,
  color: false,
  paper: 'A4',
  amount: 0.3,
  createdAt: DateTime(2026, 9, 30, 12),
);

AppPayment paymentOf({
  String status = 'Pending',
  double amount = 1.0,
  double balance = 5,
  int bonusPagesUsable = 0,
  double bonusValue = 0,
  String? promoCode,
  double promoDiscount = 0,
  double promoDiscountWithBonus = 0,
  String? promoRefusal,
  int secondsLeft = 290,
}) => AppPayment(
  id: '0f8fad5b-d9cb-469f-a165-70867728950e',
  status: status,
  amount: amount,
  expiresAt: DateTime.now().toUtc().add(Duration(seconds: secondsLeft)),
  secondsLeft: secondsLeft,
  kioskCode: 'KSK-9001',
  kioskName: 'Test kiosk',
  jobCode: '123456',
  fileName: 'hesabat.pdf',
  pages: 10,
  copies: 1,
  color: false,
  duplex: false,
  paper: 'A4',
  walletBalance: balance,
  bonusPages: bonusPagesUsable,
  bonusPagesUsable: bonusPagesUsable,
  bonusValue: bonusValue,
  promoCode: promoCode,
  promoDiscount: promoDiscount,
  promoDiscountWithBonus: promoDiscountWithBonus,
  promoRefusal: promoRefusal,
);

/// The overrides every step 4 screen needs: a signed-in user, no hub.
List<Override> step4Overrides({
  WalletState? wallet,
  List<MyJob> jobs = const [],
  bool canScan = true,
  PaymentsRepository? payments,
}) => [
  currentUserProvider.overrideWithValue(testUser),
  userHubProvider.overrideWithValue(null),
  canScanProvider.overrideWithValue(canScan),
  walletProvider.overrideWith(() => FakeWallet(wallet ?? walletOf(5))),
  myJobsProvider.overrideWith(() => FakeJobs(jobs)),
  if (payments != null) paymentsRepositoryProvider.overrideWithValue(payments),
];

Widget step4App(Widget home, {List<Override> overrides = const []}) =>
    ProviderScope(
      overrides: overrides,
      child: MaterialApp(
        theme: AppTheme.light(),
        locale: const Locale('az'),
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: home,
      ),
    );
