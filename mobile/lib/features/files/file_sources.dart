import 'package:file_picker/file_picker.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/router/app_router.dart';
import '../../l10n/app_localizations.dart';
import '../layout/layout_launcher.dart';
import '../scanner/document_scan_screen.dart';
import 'file_models.dart';
import 'files_controller.dart';

/// The ways a file gets into "Fayllarım": the files tab's upload sheet and
/// the Print tab both start here (docs/DESIGN_SYSTEM.md, "Mobil: yeni
/// vizual dil").
enum FileSource {
  /// The system picker, for a file on the phone.
  device,

  /// The same system picker: Google Drive, OneDrive and the like are listed
  /// in it when their app is on the phone (docs/DECISIONS.md, T-339). Not on
  /// the web, where the browser's picker only knows the computer's files.
  cloud,
  camera,
  scan,
  layout,
}

/// Adds a file from [source]. The upload runs in [filesControllerProvider],
/// so whichever screen started it, the files tab shows how it goes;
/// [beforeUpload] runs once a file is chosen and before it is sent - the
/// Print tab switches to the files tab there, and not when the user cancels.
Future<void> addFileFrom(
  BuildContext context,
  WidgetRef ref,
  FileSource source, {
  VoidCallback? beforeUpload,
}) async {
  final l10n = AppLocalizations.of(context);
  final controller = ref.read(filesControllerProvider.notifier);

  switch (source) {
    case FileSource.layout:
      final sources = await Navigator.of(context, rootNavigator: true)
          .push<List<LayoutSource>>(
            MaterialPageRoute(builder: (_) => const LayoutSourcesScreen()),
          );
      if (sources != null && sources.isNotEmpty && context.mounted) {
        await openNewLayout(context, ref, sources);
      }
    case FileSource.scan:
      final outcome = await context.push<ScanOutcome>(Routes.filesScanner);
      if (!context.mounted) {
        return;
      }
      if (outcome == ScanOutcome.saved) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text(l10n.docScanSaved)));
      } else if (outcome == ScanOutcome.useCamera) {
        await _photo(context, controller, beforeUpload);
      }
    case FileSource.device || FileSource.cloud:
      final picked = await _pickFile(ref);
      if (picked != null) {
        beforeUpload?.call();
        await controller.upload(picked);
      }
    case FileSource.camera:
      await _photo(context, controller, beforeUpload);
  }
}

Future<void> _photo(
  BuildContext context,
  FilesController controller,
  VoidCallback? beforeUpload,
) async {
  final path = await context.push<String>(Routes.filesCamera);
  if (path != null) {
    beforeUpload?.call();
    await controller.upload(
      PickedFile(
        name: 'photo-${DateTime.now().millisecondsSinceEpoch}.jpg',
        path: path,
        readBytes: () => throw UnsupportedError('Camera photos have a path.'),
      ),
    );
  }
}

/// The system picker, filtered to the formats the installation accepts
/// now - plus HEIC, which the phone converts before upload.
Future<PickedFile?> _pickFile(WidgetRef ref) async {
  Set<String>? accepted;

  try {
    accepted = await ref.read(filesRepositoryProvider).acceptedFormats();
  } on Exception {
    // Without the list, pick anything: the server still checks the format.
    accepted = null;
  }

  final file = await FilePicker.pickFile(
    type: accepted == null ? FileType.any : FileType.custom,
    allowedExtensions: accepted == null
        ? null
        : [...accepted, ...phoneConvertedExtensions],
  );

  if (file == null) {
    return null;
  }

  return PickedFile(
    name: file.name,
    path: kIsWeb ? null : file.path,
    readBytes: file.xFile.readAsBytes,
  );
}
