/// Files got ready in the app for a kiosk, and the personal QR that opens
/// them there (plan section 9.4; docs/API.md "Şəxsi QR və Kiosk üçün
/// hazırla"; docs/DECISIONS.md T-144).
library;

import 'dart:typed_data';

import '../session/session_models.dart';

/// The options chosen so far; a null one is chosen at the kiosk.
final class PreparedPreset {
  const PreparedPreset({
    this.copies,
    this.color,
    this.duplex,
    this.paper,
    this.pagesPerSheet,
    this.pageRange,
  });

  factory PreparedPreset.fromJson(Map<String, dynamic> json) => PreparedPreset(
    copies: json['copies'] as int?,
    color: json['color'] as bool?,
    duplex: json['duplex'] as bool?,
    paper: json['paper'] as String?,
    pagesPerSheet: json['pagesPerSheet'] as int?,
    pageRange: json['pageRange'] as String?,
  );

  static const none = PreparedPreset();

  final int? copies;
  final bool? color;
  final bool? duplex;
  final String? paper;
  final int? pagesPerSheet;
  final String? pageRange;

  bool get isEmpty =>
      copies == null &&
      color == null &&
      duplex == null &&
      paper == null &&
      pagesPerSheet == null &&
      pageRange == null;

  Map<String, dynamic> toJson() => {
    if (copies != null) 'copies': copies,
    if (color != null) 'color': color,
    if (duplex != null) 'duplex': duplex,
    if (paper != null) 'paper': paper,
    if (pagesPerSheet != null) 'pagesPerSheet': pagesPerSheet,
    if (pageRange != null) 'pageRange': pageRange,
  };
}

/// Where a preparation is.
enum PreparedState {
  ready('Ready'),
  printing('Printing'),
  printed('Printed'),
  fileUnavailable('FileUnavailable'),

  /// Paid in the app, waiting at its kiosk.
  paid('Paid');

  const PreparedState(this.wire);

  final String wire;

  static PreparedState parse(String? value) => values.firstWhere(
    (state) => state.wire == value,
    orElse: () => PreparedState.fileUnavailable,
  );
}

/// One thing got ready for a kiosk: unpaid (any kiosk) or paid (one kiosk).
final class PreparedPrint {
  const PreparedPrint({
    required this.isPaid,
    required this.id,
    required this.fileId,
    required this.fileName,
    required this.pages,
    required this.state,
    required this.createdAt,
    this.preset,
    this.options,
    this.kioskCode,
    this.kioskName,
    this.amount,
    this.jobCode,
    this.collectBy,
  });

  factory PreparedPrint.fromJson(Map<String, dynamic> json) {
    final options = json['options'] as Map<String, dynamic>?;
    final preset = json['preset'] as Map<String, dynamic>?;

    return PreparedPrint(
      isPaid: json['kind'] == 'Paid',
      id: json['id'] as String,
      fileId: json['fileId'] as String,
      fileName: json['fileName'] as String? ?? '',
      pages: json['pages'] as int? ?? 0,
      state: PreparedState.parse(json['state'] as String?),
      createdAt: DateTime.parse(json['createdAt'] as String),
      preset: preset == null ? null : PreparedPreset.fromJson(preset),
      options: options == null
          ? null
          : OrderOptions(
              copies: options['copies'] as int? ?? 1,
              color: options['color'] as bool? ?? false,
              duplex: options['duplex'] as bool? ?? false,
              paper: options['paper'] as String? ?? 'A4',
              pageRange: options['pageRange'] as String?,
              pagesPerSheet: options['pagesPerSheet'] as int? ?? 1,
            ),
      kioskCode: json['kioskCode'] as String?,
      kioskName: json['kioskName'] as String?,
      amount: (json['amount'] as num?)?.toDouble(),
      jobCode: json['jobCode'] as String?,
      collectBy: json['collectBy'] == null
          ? null
          : DateTime.parse(json['collectBy'] as String),
    );
  }

  /// Paid in the app for one kiosk; otherwise unpaid, for any kiosk.
  final bool isPaid;

  /// The preparation's id, or the paid order's job id.
  final String id;
  final String fileId;
  final String fileName;
  final int pages;
  final PreparedState state;
  final DateTime createdAt;
  final PreparedPreset? preset;
  final OrderOptions? options;
  final String? kioskCode;
  final String? kioskName;
  final double? amount;
  final String? jobCode;

  /// A paid one is cancelled and its money goes back to the wallet after this.
  final DateTime? collectBy;
}

/// The personal QR as the backend drew it.
final class MyQr {
  const MyQr({
    required this.png,
    required this.expiresAt,
    required this.rotationSeconds,
  });

  final Uint8List png;
  final DateTime expiresAt;
  final int rotationSeconds;
}
