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

  /// Still on its way: shown above the history (docs/DESIGN_SYSTEM.md 5.2).
  bool get isActive => const {
    'PendingPayment',
    'Paid',
    'Queued',
    'Printing',
    'Suspected',
  }.contains(status);

  /// Something the customer may want to report: a job that has an outcome,
  /// or whose outcome is still being worked out.
  bool get canReport => const {
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

  Future<List<MyJob>> mine() => _api.send(
    method: 'GET',
    path: 'api/print-jobs/my',
    parse: (json) => (json as List<dynamic>)
        .map((item) => MyJob.fromJson(item as Map<String, dynamic>))
        .toList(),
  );

  Future<List<MyClaim>> claims() => _api.send(
    method: 'GET',
    path: 'api/me/claims',
    parse: (json) => (json as List<dynamic>)
        .map((item) => MyClaim.fromJson(item as Map<String, dynamic>))
        .toList(),
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
