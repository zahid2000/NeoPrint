/// Scheduled printing (plan section 9.5; docs/API.md "Planlanmış çap";
/// docs/DECISIONS.md T-145).
library;

import '../session/session_models.dart';

/// Where a scheduled print is.
enum ScheduleStatus {
  /// Made, not paid yet.
  pendingPayment('PendingPayment'),
  scheduled('Scheduled'),

  /// Its time came: print it now or move it.
  awaitingUser('AwaitingUser'),
  sent('Sent'),
  cancelled('Cancelled'),

  /// Nobody answered in time; cancelled and refunded.
  expired('Expired');

  const ScheduleStatus(this.wire);

  final String wire;

  static ScheduleStatus parse(String? value) => values.firstWhere(
    (status) => status.wire == value,
    orElse: () => ScheduleStatus.cancelled,
  );

  /// Still waiting for the user: it can be printed, moved or cancelled.
  bool get isActive => this == scheduled || this == awaitingUser;
}

/// One scheduled print.
final class ScheduledPrint {
  const ScheduledPrint({
    required this.id,
    required this.jobId,
    required this.jobCode,
    required this.fileId,
    required this.fileName,
    required this.pages,
    required this.options,
    required this.kioskId,
    required this.amount,
    required this.scheduledAt,
    required this.status,
    required this.jobStatus,
    this.kioskCode,
    this.kioskName,
    this.paymentId,
    this.autoCancelAt,
  });

  factory ScheduledPrint.fromJson(Map<String, dynamic> json) {
    final options = json['options'] as Map<String, dynamic>? ?? const {};

    return ScheduledPrint(
      id: json['id'] as String,
      jobId: json['jobId'] as String,
      jobCode: json['jobCode'] as String? ?? '',
      fileId: json['fileId'] as String,
      fileName: json['fileName'] as String? ?? '',
      pages: json['pages'] as int? ?? 0,
      options: OrderOptions(
        copies: options['copies'] as int? ?? 1,
        color: options['color'] as bool? ?? false,
        duplex: options['duplex'] as bool? ?? false,
        paper: options['paper'] as String? ?? 'A4',
        pageRange: options['pageRange'] as String?,
        pagesPerSheet: options['pagesPerSheet'] as int? ?? 1,
      ),
      kioskId: json['kioskId'] as String,
      kioskCode: json['kioskCode'] as String?,
      kioskName: json['kioskName'] as String?,
      amount: (json['amount'] as num? ?? 0).toDouble(),
      scheduledAt: DateTime.parse(json['scheduledAt'] as String),
      status: ScheduleStatus.parse(json['status'] as String?),
      jobStatus: json['jobStatus'] as String? ?? '',
      paymentId: json['paymentId'] as String?,
      autoCancelAt: json['autoCancelAt'] == null
          ? null
          : DateTime.parse(json['autoCancelAt'] as String),
    );
  }

  final String id;
  final String jobId;
  final String jobCode;
  final String fileId;
  final String fileName;
  final int pages;
  final OrderOptions options;
  final String kioskId;
  final String? kioskCode;
  final String? kioskName;
  final double amount;
  final DateTime scheduledAt;
  final ScheduleStatus status;

  /// Its order's status; after [ScheduleStatus.sent], whether it printed.
  final String jobStatus;
  final String? paymentId;

  /// When an [ScheduleStatus.awaitingUser] one is cancelled and refunded.
  final DateTime? autoCancelAt;
}

/// A scheduled print just made, and the payment that pays it.
final class PlacedSchedule {
  const PlacedSchedule({
    required this.scheduleId,
    required this.paymentId,
    required this.amount,
  });

  factory PlacedSchedule.fromJson(Map<String, dynamic> json) => PlacedSchedule(
    scheduleId: json['scheduleId'] as String,
    paymentId: json['paymentId'] as String,
    amount: (json['amount'] as num).toDouble(),
  );

  final String scheduleId;
  final String paymentId;
  final double amount;
}
