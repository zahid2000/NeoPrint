/// Money in whole qəpik, so sums are exact: the server compares the amount the
/// app showed with its own to the qəpik (`expectedAmount`).
int toCents(num amount) => (amount * 100).round();

double fromCents(int cents) => cents / 100;

/// A kiosk order waiting to be paid with the app, as the confirm screen needs
/// it (`GET api/payments/{id}/app`, plan section 9.2).
final class AppPayment {
  const AppPayment({
    required this.id,
    required this.status,
    required this.amount,
    required this.expiresAt,
    required this.secondsLeft,
    this.kioskCode,
    this.kioskName,
    this.jobCode,
    this.fileName,
    required this.pages,
    required this.copies,
    required this.color,
    required this.duplex,
    this.paper,
    required this.walletBalance,
    required this.bonusPages,
    required this.bonusPagesUsable,
    required this.bonusValue,
    this.paidAt,
    this.promoCode,
    this.promoDiscount = 0,
    this.promoDiscountWithBonus = 0,
    this.promoRefusal,
    this.collectAtKiosk = false,
    this.scheduledAt,
  });

  factory AppPayment.fromJson(Map<String, dynamic> json) => AppPayment(
    id: json['id'] as String,
    status: json['status'] as String,
    amount: (json['amount'] as num).toDouble(),
    expiresAt: DateTime.parse(json['expiresAt'] as String),
    secondsLeft: json['secondsLeft'] as int? ?? 0,
    kioskCode: json['kioskCode'] as String?,
    kioskName: json['kioskName'] as String?,
    jobCode: json['jobCode'] as String?,
    fileName: json['fileName'] as String?,
    pages: json['pages'] as int? ?? 0,
    copies: json['copies'] as int? ?? 1,
    color: json['color'] as bool? ?? false,
    duplex: json['duplex'] as bool? ?? false,
    paper: json['paper'] as String?,
    walletBalance: (json['walletBalance'] as num? ?? 0).toDouble(),
    bonusPages: json['bonusPages'] as int? ?? 0,
    bonusPagesUsable: json['bonusPagesUsable'] as int? ?? 0,
    bonusValue: (json['bonusValue'] as num? ?? 0).toDouble(),
    paidAt: json['paidAt'] == null
        ? null
        : DateTime.parse(json['paidAt'] as String),
    promoCode: json['promoCode'] as String?,
    promoDiscount: (json['promoDiscount'] as num? ?? 0).toDouble(),
    promoDiscountWithBonus: (json['promoDiscountWithBonus'] as num? ?? 0)
        .toDouble(),
    promoRefusal: json['promoRefusal'] as String?,
    collectAtKiosk: json['collectAtKiosk'] as bool? ?? false,
    scheduledAt: json['scheduledAt'] == null
        ? null
        : DateTime.parse(json['scheduledAt'] as String),
  );

  final String id;

  /// `Pending` while it can be paid; `Succeeded` once paid.
  final String status;

  /// The order's price before bonus pages and the promo code.
  final double amount;
  final DateTime expiresAt;
  final int secondsLeft;
  final String? kioskCode;
  final String? kioskName;
  final String? jobCode;
  final String? fileName;
  final int pages;
  final int copies;
  final bool color;
  final bool duplex;
  final String? paper;
  final double walletBalance;
  final int bonusPages;

  /// How many bonus pages this order can take.
  final int bonusPagesUsable;

  /// What those pages take off the price.
  final double bonusValue;
  final DateTime? paidAt;
  final String? promoCode;
  final double promoDiscount;
  final double promoDiscountWithBonus;

  /// Why the order's promo code does not apply to this account, in the
  /// user's language.
  final String? promoRefusal;

  /// Paid for later (plan section 9.4): it waits at its kiosk for the user's
  /// QR rather than going into the queue.
  final bool collectAtKiosk;

  /// A scheduled print: once paid it waits for this time (plan section 9.5).
  final DateTime? scheduledAt;

  bool get isOpen => status == 'Pending';

  bool get isPaid => status == 'Succeeded';

  bool get canUseBonus => bonusPagesUsable > 0 && bonusValue > 0;

  /// The promo code's discount with or without the bonus pages: the code
  /// applies to what the pages leave (plan section 2.7).
  double promoFor({required bool useBonus}) =>
      useBonus ? promoDiscountWithBonus : promoDiscount;

  /// The money part - what the wallet or the card pays.
  double toPay({required bool useBonus}) => fromCents(
    toCents(amount) -
        (useBonus ? toCents(bonusValue) : 0) -
        toCents(promoFor(useBonus: useBonus)),
  );
}

/// A code typed in the app, as `api/promos/validate` sees it.
final class PromoPreview {
  const PromoPreview({
    required this.code,
    required this.valid,
    required this.discount,
    this.reason,
  });

  factory PromoPreview.fromJson(Map<String, dynamic> json) => PromoPreview(
    code: json['code'] as String,
    valid: json['valid'] as bool? ?? false,
    discount: (json['discount'] as num? ?? 0).toDouble(),
    reason: json['reason'] as String?,
  );

  final String code;
  final bool valid;
  final double discount;

  /// Why it does not apply, in the user's language.
  final String? reason;
}

/// Where the money part comes from.
enum PaymentSource {
  wallet('Wallet'),
  card('Card');

  const PaymentSource(this.wire);

  final String wire;
}

/// What the kiosk's QR said, after the scanner read it.
sealed class ScannedCode {
  const ScannedCode();

  static final _payLink = RegExp(
    r'^neoprint://pay/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$',
  );

  static final _kioskLink = RegExp(r'^neoprint://kiosk/([A-Za-z0-9-]{1,32})$');

  static final _shareLink = RegExp(
    r'^neoprint://share/(NP-[A-Z0-9]{6})$',
    caseSensitive: false,
  );

  /// `neoprint://pay/{id}` is an order to pay, `neoprint://kiosk/{code}` a
  /// kiosk to open a session at (plan section 2.9), `neoprint://share/{id}`
  /// a user to send a file to; anything else is not ours.
  static ScannedCode parse(String raw) {
    final text = raw.trim();

    if (_payLink.firstMatch(text) case final pay?) {
      return PayCode(pay.group(1)!);
    }

    if (_kioskLink.firstMatch(text) case final kiosk?) {
      return KioskCode(kiosk.group(1)!.toUpperCase());
    }

    if (_shareLink.firstMatch(text) case final share?) {
      return ShareCode(share.group(1)!.toUpperCase());
    }

    return const ForeignCode();
  }
}

/// The kiosk's "pay with the NeoPrint app" QR.
final class PayCode extends ScannedCode {
  const PayCode(this.paymentId);

  final String paymentId;
}

/// The kiosk's own QR in its footer: its detail opens, with "Sessiya aç".
final class KioskCode extends ScannedCode {
  const KioskCode(this.code);

  final String code;
}

/// The QR in "Paylaşılanlar": the user it names is who a file goes to.
final class ShareCode extends ScannedCode {
  const ShareCode(this.publicId);

  final String publicId;
}

/// A bank QR or any other code: not something this app pays.
final class ForeignCode extends ScannedCode {
  const ForeignCode();
}
