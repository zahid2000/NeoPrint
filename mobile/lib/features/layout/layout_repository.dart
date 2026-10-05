import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/network/api_client.dart';
import '../files/file_models.dart';
import 'layout_model.dart';

/// A layout file's layout, for the editor to open again.
final class OpenedLayout {
  const OpenedLayout(this.project, this.assets);

  /// Pictures named `asset:{key}`.
  final LayoutProject project;

  /// Every picture the layout holds, by its name.
  final List<String> assets;
}

/// Layout files (`POST files/layout`, `GET/PUT files/{id}/layout`; plan
/// section 2.11). The server keeps its own copy of every picture and draws
/// the PDF with the renderer the kiosk uses.
class LayoutRepository {
  LayoutRepository(this._api);

  final ApiClient _api;

  /// Pictures here are named `file:{id}` or `file:{id}:{page}`.
  Future<UserFile> create(LayoutProject layout, String fileName) => _api.send(
    method: 'POST',
    path: 'api/files/layout',
    body: {'layout': layout.toJson(), 'fileName': fileName},
    parse: (json) => UserFile.fromJson(json as Map<String, dynamic>),
  );

  /// Pictures it already holds keep their `asset:{key}` names.
  Future<UserFile> update(String fileId, LayoutProject layout) => _api.send(
    method: 'PUT',
    path: 'api/files/$fileId/layout',
    body: {'layout': layout.toJson()},
    parse: (json) => UserFile.fromJson(json as Map<String, dynamic>),
  );

  Future<OpenedLayout> open(String fileId) => _api.send(
    method: 'GET',
    path: 'api/files/$fileId/layout',
    parse: (json) {
      final map = json as Map<String, dynamic>;
      return OpenedLayout(
        LayoutProject.fromJson(map['layout'] as Map<String, dynamic>),
        [
          for (final asset in map['assets'] as List<dynamic>)
            (asset as Map<String, dynamic>)['name'] as String,
        ],
      );
    },
  );

  /// One of a layout's pictures; [name] is `asset:{key}`.
  Future<Uint8List> asset(String fileId, String name) => _api.send(
    method: 'GET',
    path: 'api/files/$fileId/layout/assets/${name.substring('asset:'.length)}',
    responseType: ResponseType.bytes,
    parse: (data) => Uint8List.fromList(data as List<int>),
  );
}

final layoutRepositoryProvider = Provider<LayoutRepository>(
  (ref) => LayoutRepository(ref.watch(apiClientProvider)),
);
