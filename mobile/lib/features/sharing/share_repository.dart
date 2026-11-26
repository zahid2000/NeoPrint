import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/network/api_client.dart';
import '../files/file_models.dart';
import 'share_models.dart';

final shareRepositoryProvider = Provider<ShareRepository>(
  (ref) => ShareRepository(ref.watch(apiClientProvider)),
);

/// Sharing files with other users (plan section 6, `shares`).
class ShareRepository {
  ShareRepository(this._api);

  final ApiClient _api;

  /// Sends the file as it is now. A personal document needs
  /// [confirmSensitive]: the server refuses it otherwise.
  Future<ShareItem> send({
    required String fileId,
    required String toPublicId,
    String? message,
    bool confirmSensitive = false,
  }) => _api.send(
    method: 'POST',
    path: 'api/shares',
    body: {
      'fileId': fileId,
      'toPublicId': PublicId.normalise(toPublicId),
      if (message != null && message.trim().isNotEmpty)
        'message': message.trim(),
      'confirmSensitive': confirmSensitive,
    },
    parse: (json) => ShareItem.fromJson(json as Map<String, dynamic>),
  );

  /// Newest first; a [page] of [pageSize] rows (T-413), or the newest 100.
  Future<List<ShareItem>> received({int? page, int? pageSize}) =>
      _list('received', page, pageSize);

  /// Newest first; a [page] of [pageSize] rows (T-413), or the newest 100.
  Future<List<ShareItem>> sent({int? page, int? pageSize}) =>
      _list('sent', page, pageSize);

  Future<List<ShareItem>> _list(String which, int? page, int? pageSize) =>
      _api.send(
        method: 'GET',
        path: 'api/shares/$which',
        query: {
          if (page != null) 'page': page,
          if (pageSize != null) 'pageSize': pageSize,
        },
        parse: (json) => [
          for (final item
              in (json as Map<String, dynamic>)['items'] as List<dynamic>)
            ShareItem.fromJson(item as Map<String, dynamic>),
        ],
      );

  /// "Fayllarıma saxla": the recipient's own copy. Saving again returns it.
  Future<UserFile> save(String shareId) => _api.send(
    method: 'POST',
    path: 'api/shares/$shareId/save',
    parse: (json) => UserFile.fromJson(json as Map<String, dynamic>),
  );

  /// A page of a received file before it is saved.
  Future<Uint8List> pageImage(String shareId, int page, int width) => _api.send(
    method: 'GET',
    path: 'api/shares/$shareId/pages/$page.png',
    query: {'width': width},
    responseType: ResponseType.bytes,
    parse: (data) => Uint8List.fromList(data as List<int>),
  );

  /// Who a personal QR names (T-461): their `NP-` id, to send a file to.
  Future<String> recipientByQr(String code) => _api.send(
    method: 'POST',
    path: 'api/shares/recipient-by-qr',
    body: {'code': code},
    parse: (json) => (json as Map<String, dynamic>)['publicId'] as String,
  );
}
