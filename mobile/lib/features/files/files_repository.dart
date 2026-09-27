import 'dart:typed_data';

import 'package:dio/dio.dart';

import '../../core/network/api_client.dart';
import 'file_models.dart';

/// The user's files on the server (plan section 6, `files`).
class FilesRepository {
  FilesRepository(this._api);

  final ApiClient _api;

  /// Page images already fetched, with the tag the server gave them, so a
  /// page scrolled past twice is asked for with If-None-Match and not sent
  /// again (the server answers 304).
  final Map<String, ({String etag, Uint8List bytes})> _pages = {};

  Future<FilePage> list({int page = 1, int pageSize = 50}) => _api.send(
    method: 'GET',
    path: 'api/files',
    query: {'page': page, 'pageSize': pageSize},
    parse: (json) => FilePage.fromJson(json as Map<String, dynamic>),
  );

  /// The formats the installation accepts now, for the picker's filter.
  Future<Set<String>> acceptedFormats() => _api.send(
    method: 'GET',
    path: 'api/files/formats',
    authenticated: false,
    parse: (json) =>
        (json as List<dynamic>).map((value) => value as String).toSet(),
  );

  /// Uploads a file from the device (plan section 2.2).
  ///
  /// From [path] where the platform has one - streamed from disk, so a 50 MB
  /// file is not read into memory first - and from [bytes] where it has not
  /// (the web build).
  Future<UserFile> upload({
    String? path,
    Uint8List? bytes,
    required String fileName,
    void Function(int sent, int total)? onProgress,
    CancelToken? cancelToken,
  }) async {
    assert(path != null || bytes != null, 'A path or the bytes are needed.');

    final form = FormData.fromMap({
      'file': path != null
          ? await MultipartFile.fromFile(path, filename: fileName)
          : MultipartFile.fromBytes(bytes!, filename: fileName),
      'source': 'Uploaded',
    });

    return _api.send(
      method: 'POST',
      path: 'api/files',
      body: form,
      onSendProgress: onProgress,
      cancelToken: cancelToken,
      parse: (json) => UserFile.fromJson(json as Map<String, dynamic>),
    );
  }

  Future<void> delete(String id) async {
    await _api.send(
      method: 'DELETE',
      path: 'api/files/$id',
      parse: (_) => null,
    );

    _pages.removeWhere((key, _) => key.startsWith('$id/'));
  }

  /// One page as a PNG, rendered by the server (plan section 2.0: mobile
  /// previews come from PDFium on the backend).
  ///
  /// [width] is the width wanted in pixels; the server rounds it to one of a
  /// few sizes.
  Future<Uint8List> pageImage(String id, int page, int width) async {
    final key = '$id/$page/$width';
    final cached = _pages[key];
    String? etag;

    final bytes = await _api.send<Uint8List?>(
      method: 'GET',
      path: 'api/files/$id/pages/$page.png',
      query: {'width': width},
      headers: cached == null ? null : {'If-None-Match': cached.etag},
      responseType: ResponseType.bytes,
      response: (reply) => etag = reply.headers.value('etag'),
      parse: (data) =>
          data == null ? null : Uint8List.fromList(data as List<int>),
    );

    // 304: the copy held here is still the page.
    if (bytes == null) {
      return cached!.bytes;
    }

    if (etag != null) {
      _pages[key] = (etag: etag!, bytes: bytes);
    }

    return bytes;
  }
}
