/// The app joined to one kiosk for a while (plan section 9.3; docs/API.md
/// "Sessiyalar").
library;

final class KioskSessionInfo {
  const KioskSessionInfo({
    required this.id,
    required this.kioskId,
    required this.kioskCode,
    required this.kioskName,
    required this.expiresAt,
    required this.ttlMinutes,
  });

  factory KioskSessionInfo.fromJson(Map<String, dynamic> json) =>
      KioskSessionInfo(
        id: json['id'] as String,
        kioskId: json['kioskId'] as String,
        kioskCode: json['kioskCode'] as String,
        kioskName: json['kioskName'] as String,
        expiresAt: DateTime.parse(json['expiresAt'] as String),
        ttlMinutes: json['ttlMinutes'] as int? ?? 10,
      );

  final String id;
  final String kioskId;
  final String kioskCode;
  final String kioskName;

  /// Each order placed in it moves this on (inactivity, not a fixed end).
  final DateTime expiresAt;
  final int ttlMinutes;

  bool isOverAt(DateTime now) => !expiresAt.isAfter(now);
}

/// The print options of an order (the same shape the kiosk sends).
final class OrderOptions {
  const OrderOptions({
    this.copies = 1,
    this.color = false,
    this.duplex = false,
    this.paper = 'A4',
    this.pageRange,
    this.pagesPerSheet = 1,
    this.actualSize = false,
  });

  /// The options a scan or layout must print with: 100%, its paper, one
  /// page to a sheet (plan section 2.11). Anything else starts at the defaults.
  factory OrderOptions.forPaper(String? realSizePaper) => realSizePaper == null
      ? const OrderOptions()
      : OrderOptions(paper: realSizePaper, actualSize: true);

  final int copies;
  final bool color;
  final bool duplex;

  /// `A4` or `A3`.
  final String paper;

  /// Null for every page; otherwise as typed, e.g. `1-3, 5`.
  final String? pageRange;
  final int pagesPerSheet;

  /// Real size (100%) rather than fitted to the paper: scans and layouts.
  final bool actualSize;

  OrderOptions copyWith({
    int? copies,
    bool? color,
    bool? duplex,
    String? paper,
    String? pageRange,
    bool allPages = false,
    int? pagesPerSheet,
  }) => OrderOptions(
    copies: copies ?? this.copies,
    color: color ?? this.color,
    duplex: duplex ?? this.duplex,
    paper: paper ?? this.paper,
    pageRange: allPages ? null : pageRange ?? this.pageRange,
    pagesPerSheet: pagesPerSheet ?? this.pagesPerSheet,
    actualSize: actualSize,
  );

  Map<String, dynamic> toJson() => {
    'copies': copies,
    'pageRange': pageRange,
    'color': color,
    'duplex': duplex,
    'paper': paper,
    'orientation': 'Auto',
    'pagesPerSheet': pagesPerSheet,
    'scaling': actualSize ? 'Actual' : 'Fit',
  };
}

/// What the server priced the options at.
final class OrderQuote {
  const OrderQuote({
    required this.total,
    required this.sides,
    required this.sheets,
    required this.volumeDiscount,
    required this.discountPercent,
  });

  factory OrderQuote.fromJson(Map<String, dynamic> json) {
    final price = json['price'] as Map<String, dynamic>;

    return OrderQuote(
      total: (price['finalPrice'] as num).toDouble(),
      sides: price['sides'] as int,
      sheets: price['sheets'] as int,
      volumeDiscount: (price['volumeDiscount'] as num? ?? 0).toDouble(),
      discountPercent: (price['discountPercent'] as num? ?? 0).toDouble(),
    );
  }

  final double total;
  final int sides;
  final int sheets;
  final double volumeDiscount;
  final double discountPercent;
}

/// The order placed in a session: what pays it.
final class PlacedOrder {
  const PlacedOrder({
    required this.jobId,
    required this.jobCode,
    required this.paymentId,
    required this.amount,
  });

  factory PlacedOrder.fromJson(Map<String, dynamic> json) => PlacedOrder(
    jobId: json['jobId'] as String,
    jobCode: json['jobCode'] as String,
    paymentId: json['paymentId'] as String,
    amount: (json['amount'] as num).toDouble(),
  );

  final String jobId;
  final String jobCode;
  final String paymentId;
  final double amount;
}

/// The pages a range picks out of [pageCount], as the server reads it
/// (backend `PageRange`): `1-3, 5`; null when it is not a valid range.
List<int>? parsePageRange(String? range, int pageCount) {
  if (range == null || range.trim().isEmpty) {
    return [for (var page = 1; page <= pageCount; page++) page];
  }

  final pages = <int>[];

  for (final part in range.split(',')) {
    final text = part.trim();

    if (text.isEmpty) {
      return null;
    }

    final bounds = text.split('-').map((bound) => bound.trim()).toList();

    // Digits only, as the server's NumberStyles.None: no sign, no spaces inside.
    if (bounds.length > 2 || bounds.any((bound) => !_digits.hasMatch(bound))) {
      return null;
    }

    final from = int.parse(bounds.first);
    final to = int.parse(bounds.last);

    if (from < 1 || to < from || to > pageCount) {
      return null;
    }

    for (var page = from; page <= to; page++) {
      if (!pages.contains(page)) {
        pages.add(page);
      }
    }
  }

  return pages.isEmpty ? null : pages;
}

final _digits = RegExp(r'^\d+$');
