/// Kiosks as the app finds them (plan sections 6 and 8.2; docs/API.md
/// "Kiosk axtarışı").
library;

/// What a customer can expect at a kiosk. The server already turns a kiosk
/// that stopped calling in into [offline].
enum KioskStatus {
  online,
  paused,
  maintenance,
  offline;

  static KioskStatus parse(String? value) => switch (value) {
    'Online' => online,
    'Paused' => paused,
    'Maintenance' => maintenance,
    _ => offline,
  };
}

/// One row of the search: enough for the list and a marker on the map.
final class KioskSummary {
  const KioskSummary({
    required this.id,
    required this.code,
    required this.name,
    required this.address,
    required this.lat,
    required this.lng,
    required this.status,
    this.distanceKm,
  });

  factory KioskSummary.fromJson(Map<String, dynamic> json) => KioskSummary(
    id: json['id'] as String,
    code: json['code'] as String,
    name: json['name'] as String,
    address: json['address'] as String? ?? '',
    lat: (json['lat'] as num).toDouble(),
    lng: (json['lng'] as num).toDouble(),
    status: KioskStatus.parse(json['status'] as String?),
    distanceKm: (json['distanceKm'] as num?)?.toDouble(),
  );

  final String id;

  /// `KSK-0001`.
  final String code;
  final String name;
  final String address;
  final double lat;
  final double lng;
  final KioskStatus status;

  /// From where the search was made; null without a location.
  final double? distanceKm;
}

/// How much of one paper size is left for new orders.
enum PaperState {
  ok,
  low,
  out,

  /// The tray has no counter yet: nothing is said about it.
  unknown;

  static PaperState parse(String? value) => switch (value) {
    'Ok' => ok,
    'Low' => low,
    'Out' => out,
    _ => unknown,
  };
}

final class KioskPaper {
  const KioskPaper({required this.size, required this.state, this.sheets});

  factory KioskPaper.fromJson(Map<String, dynamic> json) => KioskPaper(
    size: json['size'] as String,
    state: PaperState.parse(json['state'] as String?),
    sheets: json['availableSheets'] as int?,
  );

  /// `A4` or `A3`.
  final String size;
  final PaperState state;
  final int? sheets;
}

final class PriceTier {
  const PriceTier({required this.minSides, required this.discountPercent});

  factory PriceTier.fromJson(Map<String, dynamic> json) => PriceTier(
    minSides: json['minSides'] as int,
    discountPercent: (json['discountPercent'] as num).toDouble(),
  );

  final int minSides;
  final double discountPercent;
}

/// The rates of the kiosk's pricing profile. The order is priced again on the
/// server; these are for reading.
final class KioskPrices {
  const KioskPrices({
    required this.bwPerSide,
    required this.colorPerSide,
    required this.paperMultipliers,
    required this.minOrder,
    required this.tiers,
  });

  factory KioskPrices.fromJson(Map<String, dynamic> json) => KioskPrices(
    bwPerSide: (json['bwPerSide'] as num).toDouble(),
    colorPerSide: (json['colorPerSide'] as num).toDouble(),
    paperMultipliers: {
      for (final entry
          in (json['paperMultipliers'] as Map<String, dynamic>? ?? {}).entries)
        entry.key: (entry.value as num).toDouble(),
    },
    minOrder: (json['minOrder'] as num).toDouble(),
    tiers: [
      for (final tier in json['tiers'] as List<dynamic>? ?? const [])
        PriceTier.fromJson(tier as Map<String, dynamic>),
    ],
  );

  final double bwPerSide;
  final double colorPerSide;

  /// `A4` → 1, `A3` → 2.
  final Map<String, double> paperMultipliers;
  final double minOrder;
  final List<PriceTier> tiers;
}

/// One kiosk for a customer: what it can do, what it costs, how busy it is.
final class KioskDetail {
  const KioskDetail({
    required this.summary,
    required this.color,
    required this.duplex,
    required this.a3,
    required this.paper,
    required this.queueLength,
    this.prices,
  });

  factory KioskDetail.fromJson(Map<String, dynamic> json) {
    final capabilities = json['capabilities'] as Map<String, dynamic>? ?? {};

    return KioskDetail(
      summary: KioskSummary.fromJson(json),
      color: capabilities['color'] as bool? ?? false,
      duplex: capabilities['duplex'] as bool? ?? false,
      a3: capabilities['a3'] as bool? ?? false,
      paper: [
        for (final paper in json['paper'] as List<dynamic>? ?? const [])
          KioskPaper.fromJson(paper as Map<String, dynamic>),
      ],
      queueLength: json['queueLength'] as int? ?? 0,
      prices: json['prices'] is Map<String, dynamic>
          ? KioskPrices.fromJson(json['prices'] as Map<String, dynamic>)
          : null,
    );
  }

  final KioskSummary summary;
  final bool color;
  final bool duplex;
  final bool a3;
  final List<KioskPaper> paper;
  final int queueLength;
  final KioskPrices? prices;
}
