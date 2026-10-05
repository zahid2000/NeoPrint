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

  Future<List<ShareItem>> received() => _list('received');

  Future<List<ShareItem>> sent() => _list('sent');

  Future<List<ShareItem>> _list(String which) => _api.send(
    method: 'GET',
    path: 'api/shares/$which',
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

  /// The QR others scan to send this user a file.
  Future<Uint8List> myQr() => _api.send(
    method: 'GET',
    path: 'api/shares/qr.png',
    responseType: ResponseType.bytes,
    parse: (data) => Uint8List.fromList(data as List<int>),
  );
}
