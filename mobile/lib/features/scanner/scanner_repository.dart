import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/auth/auth_repository.dart';
import '../../core/network/api_client.dart';
import '../files/file_models.dart';
import '../layout/layout_model.dart';

/// The scan notice and saving a scan (`POST files/scan`, plan section 2.11).
class ScannerRepository {
  ScannerRepository(this._api, this._auth);

  final ApiClient _api;
  final AuthRepository _auth;

  /// The notice's version still to accept, or null when it is accepted.
  Future<String?> pendingNotice(String language) async {
    final pending = await _auth.pendingConsents(language: language, scan: true);

    return pending
        .where((consent) => consent.docType == LegalDocumentType.scanNotice)
        .firstOrNull
        ?.version;
  }

  Future<void> acceptNotice(String version) => _auth.acceptConsents([
    PendingConsent(docType: LegalDocumentType.scanNotice, version: version),
  ]);

  /// Saves the scan: the pages, how they lie on paper, the document type.
  /// The server draws the PDF; the pages are not kept.
  Future<UserFile> save({
    required Map<String, Uint8List> pages,
    required LayoutProject layout,
    required String documentType,
    double? widthMm,
    double? heightMm,
    required bool sensitive,
    required String fileName,
    void Function(int sent, int total)? onProgress,
  }) {
    final form = FormData();

    for (final MapEntry(key: name, value: bytes) in pages.entries) {
      form.files.add(
        MapEntry(
          'images',
          MultipartFile.fromBytes(
            bytes,
            filename: name,
            contentType: DioMediaType('image', 'jpeg'),
          ),
        ),
      );
    }

    form.fields.addAll([
      MapEntry('layout', jsonEncode(layout.toJson())),
      MapEntry('documentType', documentType),
      if (widthMm != null) MapEntry('physicalWidthMm', '$widthMm'),
      if (heightMm != null) MapEntry('physicalHeightMm', '$heightMm'),
      MapEntry('isSensitive', '$sensitive'),
      MapEntry('fileName', fileName),
    ]);

    return _api.send(
      method: 'POST',
      path: 'api/files/scan',
      body: form,
      onSendProgress: onProgress,
      parse: (json) => UserFile.fromJson(json as Map<String, dynamic>),
    );
  }
}

final scannerRepositoryProvider = Provider<ScannerRepository>(
  (ref) => ScannerRepository(
    ref.watch(apiClientProvider),
    ref.watch(authRepositoryProvider),
  ),
);
