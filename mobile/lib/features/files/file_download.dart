import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_client.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import 'file_models.dart';
import 'files_controller.dart';

/// The phone's own "save as" (`FileSaver.kt`, Android only): the user picks
/// the folder; no storage permission, no library (T-429).
class FileSaver {
  const FileSaver();

  static const _channel = MethodChannel('az.neoprint/file_saver');

  /// Whether this phone can save here.
  bool get supported =>
      !kIsWeb && defaultTargetPlatform == TargetPlatform.android;

  /// True when saved, false when the user closed the dialog.
  Future<bool> save(String name, String mediaType, Uint8List bytes) async =>
      await _channel.invokeMethod<bool>('save', {
        'name': name,
        'mime': mediaType,
        'bytes': bytes,
      }) ??
      false;
}

final fileSaverProvider = Provider<FileSaver>((_) => const FileSaver());

/// "Cihaza yüklə" (T-429): the file as uploaded, saved where the user
/// chooses. A spinner while it downloads; the outcome in a snackbar.
Future<void> downloadFile(
  BuildContext context,
  WidgetRef ref,
  UserFile file,
) async {
  final l10n = AppLocalizations.of(context);
  final messenger = ScaffoldMessenger.of(context);
  final navigator = Navigator.of(context, rootNavigator: true);

  unawaited(
    showDialog<void>(
      context: context,
      barrierDismissible: false,
      builder: (_) => const Center(child: CircularProgressIndicator()),
    ),
  );

  ({Uint8List bytes, String mediaType})? content;
  try {
    content = await ref.read(filesRepositoryProvider).content(file.id);
  } on ApiException catch (error) {
    messenger.showSnackBar(
      SnackBar(
        content: Text(l10n.filesDownloadFailed(apiErrorText(l10n, error))),
      ),
    );
  } finally {
    navigator.pop();
  }
  if (content == null) {
    return;
  }

  try {
    final saved = await ref
        .read(fileSaverProvider)
        .save(
          file.fileName,
          content.mediaType.split(';').first.trim(),
          content.bytes,
        );
    if (saved) {
      messenger.showSnackBar(SnackBar(content: Text(l10n.filesDownloaded)));
    }
  } on PlatformException {
    messenger.showSnackBar(
      SnackBar(content: Text(l10n.filesDownloadFailed(l10n.errorServer))),
    );
  }
}
