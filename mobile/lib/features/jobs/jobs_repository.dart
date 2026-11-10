import '../../core/network/api_client.dart';
import '../../core/network/idempotency.dart';

/// One of the user's print jobs (`GET api/print-jobs/my`).
final class MyJob {
  const MyJob({
    required this.id,
    required this.jobCode,
    required this.status,
    this.kioskCode,
    this.kioskName,
    this.fileName,
    required this.pages,
    required this.copies,
    required this.color,
    required this.paper,
    required this.amount,
    this.reason,
    required this.createdAt,
    this.finishedAt,
    this.scheduledAt,
    this.canCancel = false,
    this.kioskStopped = false,
    this.printedSides = 0,
    this.fileId,
    this.kioskId,
    this.duplex = false,
    this.pageRange,
    this.pagesPerSheet = 1,
    this.held = false,
  });

  factory MyJob.fromJson(Map<String, dynamic> json) => MyJob(
    id: json['id'] as String,
    jobCode: json['jobCode'] as String,
    status: json['status'] as String,
    kioskCode: json['kioskCode'] as String?,
    kioskName: json['kioskName'] as String?,
    fileName: json['fileName'] as String?,
    pages: json['pages'] as int? ?? 0,
    copies: json['copies'] as int? ?? 1,
    color: json['color'] as bool? ?? false,
    paper: json['paper'] as String? ?? 'A4',
    amount: (json['amount'] as num? ?? 0).toDouble(),
    reason: json['reason'] as String?,
    createdAt: DateTime.parse(json['createdAt'] as String),
    finishedAt: json['finishedAt'] == null
        ? null
        : DateTime.parse(json['finishedAt'] as String),
    scheduledAt: json['scheduledAt'] == null
        ? null
        : DateTime.parse(json['scheduledAt'] as String),
    canCancel: json['canCancel'] as bool? ?? false,
    kioskStopped: json['kioskStopped'] as bool? ?? false,
    printedSides: json['printedSides'] as int? ?? 0,
    fileId: json['fileId'] as String?,
    kioskId: json['kioskId'] as String?,
    duplex: json['duplex'] as bool? ?? false,
    pageRange: json['pageRange'] as String?,
    pagesPerSheet: json['pagesPerSheet'] as int? ?? 1,
    held: json['held'] as bool? ?? false,
  );

  final String id;
  final String jobCode;

  /// The server's job status (docs/STATE_MACHINES.md).
  final String status;
  final String? kioskCode;
  final String? kioskName;
  final String? fileName;
  final int pages;
  final int copies;
  final bool color;
  final String paper;
  final double amount;

  /// Why it stopped or was cancelled, in the user's language.
  final String? reason;
  final DateTime createdAt;
  final DateTime? finishedAt;

  /// A scheduled print still waiting for its time (T-158): when it is due.
  final DateTime? scheduledAt;

  /// The owner may cancel it now and take the money back: it waits in the
  /// queue, or stopped at the printer (plan section 9.6).
  final bool canCancel;

  /// Its kiosk cannot print at the moment: waiting may take a while.
  final bool kioskStopped;

  /// The pages printed so far: "5 / 12" while it prints (T-378).
  final int printedSides;

  /// The file in the account it was made from - "Yenidən çap et" (T-378);
  /// null for an order made at the kiosk from a flash drive.
  final String? fileId;

  /// Its kiosk, for "Növbəni izlə" (T-379).
  final String? kioskId;

  /// With [copies], [color] and [paper]: what "Yenidən çap et" starts from
  /// (T-408).
  final bool duplex;
  final String? pageRange;
  final int pagesPerSheet;

  /// Paid in the app and waiting for its owner's QR at the kiosk (T-427):
  /// not printing - nothing happens until the QR is shown there - and it
  /// can be cancelled for the money back.
  final bool held;

  /// Stopped at the printer, or waiting at a kiosk that cannot print: the
  /// owner is offered the choice - wait, or cancel and take the money back.
  bool get offersRefund =>
      canCancel && (status == 'Interrupted' || kioskStopped);

  /// Still on its way: shown above the history (docs/DESIGN_SYSTEM.md 5.2).
  /// A stopped order is too - it prints when the printer is back, unless
  /// its owner cancels it.
  bool get isActive => const {
    'PendingPayment',
    'Paid',
    'Queued',
    'Printing',
    'Interrupted',
    'Suspected',
  }.contains(status);

  /// Something the customer may want to report: a job that has an outcome,
  /// or whose outcome is still being worked out.
  bool get canReport => const {
    // Stuck "printing" with no paper going: an admin looks and may cancel it (T-385).
    'Printing',
    'Completed',
    'Interrupted',
    'Cancelled',
    'Suspected',
  }.contains(status);
}

/// A "Problem barədə bildir" message and its answer.
final class MyClaim {
  const MyClaim({
    required this.id,
    this.jobCode,
    required this.text,
    required this.status,
    this.reply,
    required this.createdAt,
  });

  factory MyClaim.fromJson(Map<String, dynamic> json) => MyClaim(
    id: json['id'] as String,
    jobCode: json['jobCode'] as String?,
    text: json['text'] as String,
    status: json['status'] as String,
    reply: json['reply'] as String?,
    createdAt: DateTime.parse(json['createdAt'] as String),
  );

  final String id;
  final String? jobCode;
  final String text;

  /// `Open`, `Answered` or `Closed`.
  final String status;
  final String? reply;
  final DateTime createdAt;
}

/// The user's jobs and their claims (plan sections 6, 2.5).
class JobsRepository {
  JobsRepository(this._api);

  final ApiClient _api;

  /// Newest first; a [page] of [pageSize] rows (T-413), or the newest 100.
  Future<List<MyJob>> mine({int? page, int? pageSize}) => _api.send(
    method: 'GET',
    path: 'api/print-jobs/my',
    query: {
      if (page != null) 'page': page,
      if (pageSize != null) 'pageSize': pageSize,
    },
    parse: (json) => (json as List<dynamic>)
        .map((item) => MyJob.fromJson(item as Map<String, dynamic>))
        .toList(),
  );

  /// Newest first; a [page] of [pageSize] rows (T-413), or the newest 100.
  Future<List<MyClaim>> claims({int? page, int? pageSize}) => _api.send(
    method: 'GET',
    path: 'api/me/claims',
    query: {
      if (page != null) 'page': page,
      if (pageSize != null) 'pageSize': pageSize,
    },
    parse: (json) => (json as List<dynamic>)
        .map((item) => MyClaim.fromJson(item as Map<String, dynamic>))
        .toList(),
  );

  /// Cancels a waiting order of the user's own; what was paid goes back.
  /// Cancelling twice is harmless.
  Future<void> cancel(String jobId) => _api.send(
    method: 'POST',
    path: 'api/print-jobs/my/$jobId/cancel',
    parse: (_) {},
  );

  /// "Müraciəti davam etdir": an answered claim is open for the admins again (T-385).
  Future<void> reopenClaim(String claimId, {required String idempotencyKey}) =>
      _api.send(
        method: 'POST',
        path: 'api/me/claims/$claimId/reopen',
        headers: {idempotencyKeyHeader: idempotencyKey},
        parse: (_) {},
      );

  Future<void> report(
    String jobId,
    String text, {
    required String idempotencyKey,
  }) => _api.send(
    method: 'POST',
    path: 'api/me/claims',
    headers: {idempotencyKeyHeader: idempotencyKey},
    body: {'jobId': jobId, 'text': text},
    parse: (_) {},
  );
}
