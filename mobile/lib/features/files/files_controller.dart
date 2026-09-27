import 'dart:async';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/network/api_client.dart';
import 'file_models.dart';
import 'files_repository.dart';
import 'heic_converter.dart';

final filesRepositoryProvider = Provider<FilesRepository>(
  (ref) => FilesRepository(ref.watch(apiClientProvider)),
);

final heicConverterProvider = Provider<HeicConverter>(
  (_) => const HeicConverter(),
);

final filesControllerProvider = NotifierProvider<FilesController, FilesState>(
  FilesController.new,
);

/// A file the user picked or photographed, before it is uploaded.
final class PickedFile {
  const PickedFile({required this.name, this.path, required this.readBytes});

  final String name;

  /// On the phone; null in the web build, where only the bytes are available.
  final String? path;

  final Future<Uint8List> Function() readBytes;
}

/// Why an upload did not go through. Worded by the screen, not here.
sealed class UploadError {
  const UploadError();
}

/// The server refused: too large, a format switched off, quota full.
final class ServerUploadError extends UploadError {
  const ServerUploadError(this.error);
  final ApiException error;
}

final class HeicUploadError extends UploadError {
  const HeicUploadError(this.reason);
  final HeicFailure reason;
}

final class NetworkUploadError extends UploadError {
  const NetworkUploadError();
}

/// One upload in progress, or one that failed and waits to be dismissed.
final class UploadItem {
  const UploadItem({
    required this.id,
    required this.fileName,
    required this.cancelToken,
    this.progress = 0,
    this.error,
  });

  final String id;
  final String fileName;
  final CancelToken cancelToken;

  /// 0 to 1; 0 also while a HEIC photo is being converted.
  final double progress;
  final UploadError? error;

  UploadItem copyWith({double? progress, UploadError? error}) => UploadItem(
    id: id,
    fileName: fileName,
    cancelToken: cancelToken,
    progress: progress ?? this.progress,
    error: error ?? this.error,
  );
}

final class FilesState {
  const FilesState({
    this.files = const [],
    this.loading = true,
    this.error,
    this.uploads = const [],
  });

  final List<UserFile> files;

  /// True only until the first list arrives: later refreshes keep the list
  /// on screen instead of blanking it.
  final bool loading;

  /// Why the list could not be loaded, worded by the server or the client.
  final ApiException? error;

  final List<UploadItem> uploads;

  FilesState copyWith({
    List<UserFile>? files,
    bool? loading,
    ApiException? error,
    bool clearError = false,
    List<UploadItem>? uploads,
  }) => FilesState(
    files: files ?? this.files,
    loading: loading ?? this.loading,
    error: clearError ? null : error ?? this.error,
    uploads: uploads ?? this.uploads,
  );
}

/// The files tab: the list, uploads, and keeping an eye on files still being
/// converted (plan section 8.2).
class FilesController extends Notifier<FilesState> {
  /// How often the list is asked again while a file is being converted.
  /// Conversion takes seconds to a minute; step 3 replaces this with the
  /// user hub's events.
  static const pollInterval = Duration(seconds: 3);

  Timer? _poll;
  int _nextUploadId = 0;

  FilesRepository get _repository => ref.read(filesRepositoryProvider);

  @override
  FilesState build() {
    ref.onDispose(() => _poll?.cancel());
    Future.microtask(load);
    return const FilesState();
  }

  Future<void> load() async {
    try {
      final page = await _repository.list();
      state = state.copyWith(
        files: page.items,
        loading: false,
        clearError: true,
      );
    } on ApiException catch (error) {
      state = state.copyWith(loading: false, error: error);
    }

    _schedulePoll();
  }

  Future<void> upload(PickedFile file) async {
    final item = UploadItem(
      id: 'upload-${_nextUploadId++}',
      fileName: file.name,
      cancelToken: CancelToken(),
    );

    state = state.copyWith(uploads: [...state.uploads, item]);

    try {
      var name = file.name;
      var path = file.path;
      Uint8List? bytes;

      if (phoneConvertedExtensions.contains(extensionOf(name))) {
        if (path == null) {
          throw const HeicConversionException(HeicFailure.unsupported);
        }

        path = await ref.read(heicConverterProvider).toJpeg(path);
        name = '${name.substring(0, name.lastIndexOf('.'))}.jpg';
      }

      if (path == null) {
        bytes = await file.readBytes();
      }

      await _repository.upload(
        path: path,
        bytes: bytes,
        fileName: name,
        cancelToken: item.cancelToken,
        onProgress: (sent, total) {
          if (total > 0) {
            _updateUpload(
              item.id,
              (upload) => upload.copyWith(progress: sent / total),
            );
          }
        },
      );

      _removeUpload(item.id);
      await load();
    } on HeicConversionException catch (error) {
      _updateUpload(
        item.id,
        (upload) => upload.copyWith(error: HeicUploadError(error.reason)),
      );
    } on ApiException catch (error) {
      if (item.cancelToken.isCancelled) {
        _removeUpload(item.id);
      } else {
        _updateUpload(
          item.id,
          (upload) => upload.copyWith(
            error: error.isNetworkError
                ? const NetworkUploadError()
                : ServerUploadError(error),
          ),
        );
      }
    }
  }

  void cancelUpload(String id) {
    for (final upload in state.uploads.where((upload) => upload.id == id)) {
      upload.cancelToken.cancel();
    }

    _removeUpload(id);
  }

  void dismissUpload(String id) => _removeUpload(id);

  /// Deletes a file everywhere it is stored.
  ///
  /// Returns why it was refused, or null.
  Future<ApiException?> delete(UserFile file) async {
    try {
      await _repository.delete(file.id);
      state = state.copyWith(
        files: state.files
            .where((candidate) => candidate.id != file.id)
            .toList(),
      );
      return null;
    } on ApiException catch (error) {
      return error;
    }
  }

  void _schedulePoll() {
    _poll?.cancel();

    if (state.files.any((file) => !file.status.isSettled)) {
      _poll = Timer(pollInterval, load);
    }
  }

  void _updateUpload(String id, UploadItem Function(UploadItem) change) {
    state = state.copyWith(
      uploads: [
        for (final upload in state.uploads)
          if (upload.id == id) change(upload) else upload,
      ],
    );
  }

  void _removeUpload(String id) {
    state = state.copyWith(
      uploads: state.uploads.where((upload) => upload.id != id).toList(),
    );
  }
}
