import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/features/feature_flags.dart';
import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/network/idempotency.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../../shared/widgets/neo_primary_button.dart';
import '../jobs/jobs_controller.dart';
import '../wallet/wallet_controller.dart';
import 'payment_models.dart';
import 'payments_repository.dart';

final paymentsRepositoryProvider = Provider<PaymentsRepository>(
  (ref) => PaymentsRepository(ref.watch(apiClientProvider)),
);

/// `m:ss`, the way the kiosk writes its countdown.
String formatCountdown(int seconds) {
  final safe = seconds < 0 ? 0 : seconds;
  return '${safe ~/ 60}:${(safe % 60).toString().padLeft(2, '0')}';
}

/// Paying a kiosk order with the app (plan section 9.2; docs/DESIGN_SYSTEM.md
/// section 4, "Ödəniş təsdiqi"): what is being paid for, bonus pages, the
/// promo code, where the money comes from, and one button.
class PayScreen extends ConsumerStatefulWidget {
  const PayScreen({super.key, required this.paymentId});

  final String paymentId;

  @override
  ConsumerState<PayScreen> createState() => _PayScreenState();
}

class _PayScreenState extends ConsumerState<PayScreen> {
  AppPayment? _payment;
  ApiException? _loadError;
  bool _loading = true;

  bool _useBonus = true;
  PaymentSource? _source;

  bool _paying = false;
  String? _payError;

  /// One key per attempt: kept for a retry after a lost answer, renewed only
  /// once the server has said no.
  String _key = newIdempotencyKey();

  Timer? _ticker;
  int _secondsLeft = 0;

  // A promo code typed here, for an order that got none at the kiosk.
  final _promoField = TextEditingController();
  bool _promoOpen = false;
  PromoPreview? _typedPromo;
  String? _promoProblem;
  bool _checkingPromo = false;
  int _promoCheck = 0;

  @override
  void initState() {
    super.initState();
    unawaited(_load());
  }

  @override
  void dispose() {
    _ticker?.cancel();
    _promoField.dispose();
    super.dispose();
  }

  /// What the bonus pages leave: the amount a promo code applies to.
  double _afterBonus(AppPayment payment) => fromCents(
    toCents(payment.amount) -
        (_useBonus && payment.canUseBonus ? toCents(payment.bonusValue) : 0),
  );

  /// The money part, with the kiosk's code or the one typed here.
  double _toPay(AppPayment payment) {
    final typed = _typedPromo;

    if (payment.promoCode == null && typed != null) {
      return fromCents(toCents(_afterBonus(payment)) - toCents(typed.discount));
    }

    return payment.toPay(useBonus: _useBonus && payment.canUseBonus);
  }

  Future<void> _checkPromo({String? code}) async {
    final payment = _payment;
    final typed = (code ?? _promoField.text).trim().toUpperCase();

    if (payment == null || typed.isEmpty) {
      return;
    }

    final check = ++_promoCheck;
    setState(() {
      _checkingPromo = true;
      _promoProblem = null;
    });

    String? problem;
    PromoPreview? preview;

    try {
      preview = await ref
          .read(paymentsRepositoryProvider)
          .previewPromo(typed, _afterBonus(payment));

      if (!preview.valid) {
        problem = preview.reason;
        preview = null;
      }
    } on ApiException catch (error) {
      if (mounted) {
        problem = apiErrorText(AppLocalizations.of(context), error);
      }
    }

    // A newer check (the bonus switch moved meanwhile) wins.
    if (!mounted || check != _promoCheck) {
      return;
    }

    setState(() {
      _checkingPromo = false;
      _typedPromo = preview;
      _promoProblem = problem;
    });
  }

  void _removePromo() => setState(() {
    _promoCheck++;
    _typedPromo = null;
    _promoProblem = null;
    _promoField.clear();
  });

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _loadError = null;
    });

    try {
      final payment = await ref
          .read(paymentsRepositoryProvider)
          .get(widget.paymentId);

      if (!mounted) {
        return;
      }

      setState(() {
        _payment = payment;
        _loading = false;
        _useBonus = payment.canUseBonus && _useBonus;
        _source ??= payment.walletBalance >= _toPay(payment)
            ? PaymentSource.wallet
            : PaymentSource.card;
        _secondsLeft = payment.secondsLeft;
      });

      _startTicker();
    } on ApiException catch (error) {
      if (mounted) {
        setState(() {
          _loadError = error;
          _loading = false;
        });
      }
    }
  }

  void _startTicker() {
    _ticker?.cancel();
    final payment = _payment;

    if (payment == null || !payment.isOpen) {
      return;
    }

    // From the server's own count, not its clock against the phone's: a
    // phone a few minutes off would otherwise show the wrong time left.
    final deadline = DateTime.now().add(Duration(seconds: payment.secondsLeft));

    _ticker = Timer.periodic(const Duration(seconds: 1), (_) {
      final left = deadline.difference(DateTime.now()).inSeconds;

      setState(() => _secondsLeft = left < 0 ? 0 : left);

      if (left <= 0) {
        _ticker?.cancel();
      }
    });
  }

  Future<void> _pay() async {
    final payment = _payment;
    final source = _source;
    if (payment == null || source == null) {
      return;
    }

    setState(() {
      _paying = true;
      _payError = null;
    });

    try {
      final paid = await ref
          .read(paymentsRepositoryProvider)
          .pay(
            payment.id,
            source: source,
            useBonus: _useBonus && payment.canUseBonus,
            expectedAmount: _toPay(payment),
            idempotencyKey: _key,
            promoCode: payment.promoCode == null ? _typedPromo?.code : null,
          );

      ref.invalidate(myJobsProvider);
      ref.invalidate(walletProvider);

      if (mounted) {
        _ticker?.cancel();
        setState(() {
          _payment = paid;
          _paying = false;
        });
      }
    } on ApiException catch (error) {
      if (!mounted) {
        return;
      }

      final l10n = AppLocalizations.of(context);

      // A refusal is final for this attempt; a lost answer keeps the key so
      // trying again cannot take the money twice.
      if (error.kind == ApiErrorKind.server && error.statusCode != 503) {
        _key = newIdempotencyKey();
      }

      // A typed code the payment refused is shown on its field, not above.
      final promoRefused = error.errorFor('promoCode');

      setState(() {
        _paying = false;
        if (promoRefused != null) {
          _typedPromo = null;
          _promoProblem = promoRefused;
        } else {
          _payError = apiErrorText(l10n, error);
        }
      });

      // A price that moved is shown again before anything else is tried.
      if (error.statusCode == 409) {
        await _load();
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final payment = _payment;

    return Scaffold(
      appBar: AppBar(title: Text(l10n.payTitle)),
      body: SafeArea(
        child: switch ((payment, _loadError)) {
          (null, final ApiException error) when error.statusCode == 404 =>
            NeoEmptyState(icon: Icons.qr_code_2, message: l10n.payNotFound),
          (null, final ApiException error) => NeoEmptyState(
            icon: Icons.cloud_off_outlined,
            message: apiErrorText(l10n, error),
            action: OutlinedButton(
              onPressed: _load,
              child: Text(l10n.commonRetry),
            ),
          ),
          (null, _) when _loading => const Center(
            child: CircularProgressIndicator(),
          ),
          (final AppPayment paid, _) when paid.isPaid => _Paid(payment: paid),
          (final AppPayment open, _) when !open.isOpen || _secondsLeft <= 0 =>
            NeoEmptyState(
              icon: Icons.timer_off_outlined,
              message: l10n.payExpiredTitle,
              hint: l10n.payExpiredBody,
              action: OutlinedButton(
                onPressed: () => context.go(Routes.home),
                child: Text(l10n.payBackHome),
              ),
            ),
          (final AppPayment open, _) => _form(context, open),
          _ => const SizedBox.shrink(),
        },
      ),
    );
  }

  /// "Promokodunuz var?": a field that opens on request, and the code's line
  /// once it applies (docs/DESIGN_SYSTEM.md section 4).
  Widget _promoSection(BuildContext context, String Function(double) money) {
    final l10n = AppLocalizations.of(context);
    final typed = _typedPromo;

    if (typed != null) {
      return Row(
        children: [
          Expanded(
            child: NeoBanner(
              message: l10n.payPromoApplied(typed.code, money(typed.discount)),
              tone: StatusTone.success,
            ),
          ),
          IconButton(
            onPressed: _paying ? null : _removePromo,
            tooltip: l10n.payPromoRemove,
            icon: const Icon(Icons.close),
          ),
        ],
      );
    }

    if (!_promoOpen) {
      return Align(
        alignment: AlignmentDirectional.centerStart,
        child: TextButton.icon(
          onPressed: () => setState(() => _promoOpen = true),
          icon: const Icon(Icons.local_offer_outlined),
          label: Text(l10n.payPromoAdd),
        ),
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // One column, as every form on the phone (docs/DESIGN_SYSTEM.md 5.2):
        // the theme's buttons are full width.
        TextField(
          controller: _promoField,
          enabled: !_paying && !_checkingPromo,
          textCapitalization: TextCapitalization.characters,
          decoration: InputDecoration(labelText: l10n.payPromoField),
          onSubmitted: (_) => _checkPromo(),
        ),
        if (_promoProblem != null) _PromoProblem(text: _promoProblem!),
        const SizedBox(height: Spacing.x2),
        OutlinedButton(
          onPressed: _paying || _checkingPromo ? null : _checkPromo,
          child: Text(l10n.payPromoApply),
        ),
      ],
    );
  }

  Widget _form(BuildContext context, AppPayment payment) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final language = Localizations.localeOf(context).languageCode;
    String money(double amount) => formatMoney(amount, language: language);

    final toPay = _toPay(payment);
    final promo = payment.promoFor(useBonus: _useBonus && payment.canUseBonus);
    final canTypePromo =
        payment.promoCode == null &&
        _afterBonus(payment) > 0 &&
        ref.watch(featureOnProvider(FeatureKey.promoCodes));
    final walletShort = payment.walletBalance < toPay;
    final low = _secondsLeft <= 60;

    // The list scrolls; the one primary button stays at the bottom, where a
    // thumb reaches it on any screen size.
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Expanded(
          child: ListView(
            padding: const EdgeInsets.all(Spacing.x4),
            children: [
              Text(
                money(toPay),
                textAlign: TextAlign.center,
                style: theme.textTheme.displaySmall?.copyWith(
                  fontWeight: FontWeights.semibold,
                  fontFeatures: const [FontFeature.tabularFigures()],
                ),
              ),
              const SizedBox(height: Spacing.x1),
              Row(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  if (low) ...[
                    Icon(
                      Icons.warning_amber_outlined,
                      size: 18,
                      color: tokens.warning,
                    ),
                    const SizedBox(width: Spacing.x1),
                  ],
                  // Flexible: on a narrow phone, or in a longer language, the
                  // sentence goes onto a second line instead of off the screen.
                  Flexible(
                    child: Text(
                      l10n.payTimeLeft(formatCountdown(_secondsLeft)),
                      textAlign: TextAlign.center,
                      style: theme.textTheme.bodyMedium?.copyWith(
                        color: low ? tokens.warning : tokens.neutral600,
                        fontFeatures: const [FontFeature.tabularFigures()],
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: Spacing.x4),
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(Spacing.x4),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        payment.fileName ?? payment.jobCode ?? '',
                        style: theme.textTheme.titleMedium,
                      ),
                      const SizedBox(height: Spacing.x1),
                      Text(
                        '${l10n.payKiosk}: ${[if (payment.kioskName != null) payment.kioskName!, if (payment.kioskCode != null) payment.kioskCode!].join(' · ')}',
                        style: theme.textTheme.bodySmall,
                      ),
                      const SizedBox(height: Spacing.x2),
                      Text(
                        [
                          l10n.jobPagesCopies(payment.pages, payment.copies),
                          payment.color ? l10n.payColor : l10n.payBlackWhite,
                          if (payment.paper != null) payment.paper!,
                        ].join(' · '),
                        style: theme.textTheme.bodyMedium,
                      ),
                    ],
                  ),
                ),
              ),
              if (payment.canUseBonus) ...[
                const SizedBox(height: Spacing.x2),
                SwitchListTile(
                  value: _useBonus,
                  onChanged: _paying
                      ? null
                      : (value) {
                          setState(() => _useBonus = value);
                          // The code applies to what the pages leave: check again.
                          final typed = _typedPromo;
                          if (typed != null) {
                            unawaited(_checkPromo(code: typed.code));
                          }
                        },
                  title: Text(
                    l10n.payUseBonus(
                      payment.bonusPagesUsable,
                      money(payment.bonusValue),
                    ),
                  ),
                ),
              ],
              if (payment.promoCode != null && promo > 0) ...[
                const SizedBox(height: Spacing.x2),
                NeoBanner(
                  message: l10n.payPromoApplied(
                    payment.promoCode!,
                    money(promo),
                  ),
                  tone: StatusTone.success,
                ),
              ] else if (payment.promoRefusal != null) ...[
                const SizedBox(height: Spacing.x2),
                NeoBanner(
                  message: l10n.payPromoRefused(payment.promoRefusal!),
                  tone: StatusTone.warning,
                ),
              ],
              if (canTypePromo) ...[
                const SizedBox(height: Spacing.x2),
                _promoSection(context, money),
              ],
              if (toPay > 0) ...[
                const SizedBox(height: Spacing.x4),
                Text(l10n.paySourceTitle, style: theme.textTheme.titleMedium),
                const SizedBox(height: Spacing.x2),
                RadioGroup<PaymentSource>(
                  groupValue: _source,
                  onChanged: (value) {
                    if (!_paying) {
                      setState(() => _source = value);
                    }
                  },
                  child: Column(
                    children: [
                      _SourceTile(
                        source: PaymentSource.wallet,
                        icon: Icons.account_balance_wallet_outlined,
                        title: l10n.payFromWallet,
                        subtitle: l10n.payWalletBalance(
                          money(payment.walletBalance),
                        ),
                        warning: walletShort ? l10n.payWalletShort : null,
                      ),
                      const SizedBox(height: Spacing.x2),
                      _SourceTile(
                        source: PaymentSource.card,
                        icon: Icons.credit_card,
                        title: l10n.payByCard,
                        subtitle: l10n.payByCardHint,
                      ),
                    ],
                  ),
                ),
              ],
            ],
          ),
        ),
        Padding(
          padding: const EdgeInsets.all(Spacing.x4),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              // Next to the button that caused it, never scrolled out of view.
              if (_payError != null) ...[
                NeoBanner.error(message: _payError!),
                const SizedBox(height: Spacing.x3),
              ],
              NeoPrimaryButton(
                label: toPay > 0
                    ? l10n.payButton(money(toPay))
                    : l10n.payWithBonusOnly,
                onPressed: _source == null && toPay > 0 ? null : _pay,
                busy: _paying,
              ),
            ],
          ),
        ),
      ],
    );
  }
}

class _PromoProblem extends StatelessWidget {
  const _PromoProblem({required this.text});

  final String text;

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;

    return Padding(
      padding: const EdgeInsets.only(top: Spacing.x2),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(Icons.warning_amber_outlined, size: 18, color: tokens.warning),
          const SizedBox(width: Spacing.x1),
          Expanded(
            child: Text(
              AppLocalizations.of(context).payPromoNotApplied(text),
              style: TextStyle(color: tokens.warning),
            ),
          ),
        ],
      ),
    );
  }
}

class _SourceTile extends StatelessWidget {
  const _SourceTile({
    required this.source,
    required this.icon,
    required this.title,
    required this.subtitle,
    this.warning,
  });

  final PaymentSource source;
  final IconData icon;
  final String title;
  final String subtitle;
  final String? warning;

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;

    return Card(
      margin: EdgeInsets.zero,
      child: RadioListTile<PaymentSource>(
        value: source,
        secondary: Icon(icon),
        title: Text(title),
        subtitle: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(subtitle),
            if (warning != null)
              Padding(
                padding: const EdgeInsets.only(top: Spacing.x1),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Icon(
                      Icons.warning_amber_outlined,
                      size: 16,
                      color: tokens.warning,
                    ),
                    const SizedBox(width: Spacing.x1),
                    Expanded(
                      child: Text(
                        warning!,
                        style: TextStyle(color: tokens.warning),
                      ),
                    ),
                  ],
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class _Paid extends StatelessWidget {
  const _Paid({required this.payment});

  final AppPayment payment;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final tokens = context.tokens;
    final theme = Theme.of(context);

    return Center(
      child: SingleChildScrollView(
        padding: const EdgeInsets.all(Spacing.x6),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.check_circle, size: 72, color: tokens.success),
            const SizedBox(height: Spacing.x4),
            Text(l10n.paySuccessTitle, style: theme.textTheme.headlineSmall),
            const SizedBox(height: Spacing.x2),
            Text(
              switch (payment) {
                AppPayment(scheduledAt: final at?) => l10n.payScheduledBody(
                  formatDateTime(
                    at,
                    today: l10n.commonToday,
                    yesterday: l10n.commonYesterday,
                  ),
                ),
                AppPayment(collectAtKiosk: true) => l10n.payCollectAtKioskBody(
                  payment.kioskCode ?? '',
                ),
                _ => l10n.paySuccessBody(payment.jobCode ?? ''),
              },
              textAlign: TextAlign.center,
              style: theme.textTheme.bodyLarge,
            ),
            const SizedBox(height: Spacing.x6),
            if (payment.scheduledAt != null)
              NeoPrimaryButton(
                label: l10n.payToSchedules,
                onPressed: () => context.go(Routes.scheduled),
              )
            else
              NeoPrimaryButton(
                label: l10n.payBackHome,
                onPressed: () => context.go(Routes.home),
              ),
          ],
        ),
      ),
    );
  }
}
