import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/network/api_client.dart';
import '../../core/realtime/user_hub_provider.dart';

enum DataExportStatus {
  preparing('Preparing'),
  ready('Ready'),
  failed('Failed'),
  expired('Expired');

  const DataExportStatus(this.wire);

  final String wire;

  static DataExportStatus parse(String? value) => values.firstWhere(
    (status) => status.wire == value,
    orElse: () => DataExportStatus.expired,
  );
}

/// The latest "Məlumatlarımı yüklə" archive (docs/API.md "Məxfilik").
class DataExport {
  const DataExport({
    required this.id,
    required this.status,
    required this.requestedAt,
    required this.readyAt,
    required this.expiresAt,
    required this.sizeBytes,
    required this.includesFiles,
    required this.downloadUrl,
    required this.failureReason,
  });

  factory DataExport.fromJson(Map<String, dynamic> json) => DataExport(
    id: json['id'] as String,
    status: DataExportStatus.parse(json['status'] as String?),
    requestedAt: DateTime.parse(json['requestedAt'] as String),
    readyAt: _date(json['readyAt']),
    expiresAt: _date(json['expiresAt']),
    sizeBytes: (json['sizeBytes'] as num?)?.toInt() ?? 0,
    includesFiles: json['includesFiles'] as bool? ?? true,
    downloadUrl: json['downloadUrl'] as String?,
    failureReason: json['failureReason'] as String?,
  );

  final String id;
  final DataExportStatus status;
  final DateTime requestedAt;
  final DateTime? readyAt;
  final DateTime? expiresAt;
  final int sizeBytes;
  final bool includesFiles;

  /// Relative to the API; only while the archive can be downloaded.
  final String? downloadUrl;

  /// Already in the app's language.
  final String? failureReason;

  bool get canDownload =>
      status == DataExportStatus.ready && downloadUrl != null;

  static DateTime? _date(Object? value) =>
      value == null ? null : DateTime.parse(value as String);
}

class DataExportRepository {
  DataExportRepository(this._api);

  final ApiClient _api;

  /// The latest archive, or null when none was ever asked for.
  Future<DataExport?> latest() => _api.send(
    method: 'GET',
    path: 'api/users/me/export',
    // 204 when there is none: dio hands over null or an empty string.
    parse: (json) =>
        json is Map<String, dynamic> ? DataExport.fromJson(json) : null,
  );

  /// Asks for an archive; one still being made or still valid comes back instead.
  Future<DataExport> request() => _api.send(
    method: 'POST',
    path: 'api/users/me/export',
    parse: (json) => DataExport.fromJson(json as Map<String, dynamic>),
  );
}

final dataExportRepositoryProvider = Provider<DataExportRepository>(
  (ref) => DataExportRepository(ref.watch(apiClientProvider)),
);

/// The archive the screen shows; read again when its ExportReady
/// notification arrives.
final dataExportProvider =
    AsyncNotifierProvider<DataExportController, DataExport?>(
      DataExportController.new,
    );

class DataExportController extends AsyncNotifier<DataExport?> {
  DataExportRepository get _repository =>
      ref.read(dataExportRepositoryProvider);

  @override
  Future<DataExport?> build() {
    final ready = ref
        .watch(userHubEventsProvider)
        .where(
          (event) =>
              event.target == 'Notification' &&
              event.payload['type'] == 'ExportReady',
        )
        .listen((_) => unawaited(refresh()));
    ref.onDispose(() => unawaited(ready.cancel()));

    return _repository.latest();
  }

  Future<void> refresh() async {
    state = await AsyncValue.guard(_repository.latest);
  }

  Future<void> request() async {
    state = await AsyncValue.guard(_repository.request);
  }
}

/// Where the browser fetches [export] from: the API's own address plus the
/// signed path.
Uri downloadUri(DataExport export) =>
    Uri.parse(apiBaseUrl).resolve(export.downloadUrl!);
