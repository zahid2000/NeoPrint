import 'package:file_picker/file_picker.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/features/readiness.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../layout/layout_launcher.dart';
import '../scanner/document_scan_screen.dart';
import '../scanner/document_scanner.dart';
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

/// The ways in, as a bottom sheet: the files tab and "Fayllarımdan seç" in
/// the Print tab (T-353) offer the same ones. Null when nothing was chosen.
Future<FileSource?> chooseFileSource(BuildContext context) async {
  final l10n = AppLocalizations.of(context);

  return showModalBottomSheet<FileSource>(
    context: context,
    showDragHandle: true,
    // Four ways in: scrollable, so a small or turned phone still reaches all.
    isScrollControlled: true,
    builder: (context) => SafeArea(
      child: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            ListTile(
              leading: const Icon(Icons.folder_open_outlined),
              title: Text(l10n.filesPickFile),
              subtitle: Text(l10n.filesPickFileHint),
              onTap: () => Navigator.pop(context, FileSource.device),
            ),
            // Google Drive, OneDrive: the same system picker (T-339).
            if (!kIsWeb)
              ListTile(
                leading: const Icon(Icons.cloud_upload_outlined),
                title: Text(l10n.printHubFromCloud),
                subtitle: Text(l10n.printHubFromCloudHint),
                onTap: () => Navigator.pop(context, FileSource.cloud),
              ),
            if (!kIsWeb)
              ListTile(
                leading: const Icon(Icons.photo_camera_outlined),
                title: Text(l10n.filesTakePhoto),
                subtitle: Text(l10n.filesTakePhotoHint),
                onTap: () => Navigator.pop(context, FileSource.camera),
              ),
            // The phone scanner (plan section 2.11): not on the web.
            if (Readiness.documentScan && DocumentScanner.isSupportedPlatform)
              ListTile(
                leading: const Icon(Icons.document_scanner_outlined),
                title: Text(l10n.filesScanDocument),
                subtitle: Text(l10n.filesScanDocumentHint),
                onTap: () => Navigator.pop(context, FileSource.scan),
              ),
            // Pictures and PDF pages on one sheet (plan section 2.11).
            ListTile(
              leading: const Icon(Icons.dashboard_customize_outlined),
              title: Text(l10n.filesNewLayout),
              subtitle: Text(l10n.filesNewLayoutHint),
              onTap: () => Navigator.pop(context, FileSource.layout),
            ),
            const SizedBox(height: Spacing.x4),
          ],
        ),
      ),
    ),
  );
}

/// Adds a file from [source].
///
/// From the files tab ([thenPrint] false) the upload runs in
/// [filesControllerProvider] and shows on the list. From the Print tab
/// ([thenPrint] true) the user is printing (T-347): the chosen file uploads
/// on its own screen, which opens the file's preview - and from there its
/// print settings - once it is ready; a saved scan opens its preview at once.
Future<void> addFileFrom(
  BuildContext context,
  WidgetRef ref,
  FileSource source, {
  bool thenPrint = false,
}) async {
  final l10n = AppLocalizations.of(context);
  final controller = ref.read(filesControllerProvider.notifier);

  Future<void> send(PickedFile picked) async {
    if (thenPrint) {
      await context.push(Routes.printUpload, extra: picked);
    } else {
      await controller.upload(picked);
    }
  }

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
        if (thenPrint) {
          await _openNewestScan(context, ref);
        } else {
          ScaffoldMessenger.of(context)
              .showSnackBar(SnackBar(content: Text(l10n.docScanSaved)));
        }
      } else if (outcome == ScanOutcome.useCamera) {
        final photo = await _photo(context);
        if (photo != null && context.mounted) {
          await send(photo);
        }
      }
    case FileSource.device || FileSource.cloud:
      final picked = await _pickFile(ref);
      if (picked != null && context.mounted) {
        await send(picked);
      }
    case FileSource.camera:
      final photo = await _photo(context);
      if (photo != null && context.mounted) {
        await send(photo);
      }
  }
}

/// The scan just saved: the newest scan on the list, opened for printing.
Future<void> _openNewestScan(BuildContext context, WidgetRef ref) async {
  final controller = ref.read(filesControllerProvider.notifier);
  await controller.load();

  final scans = [
    for (final file in ref.read(filesControllerProvider).files)
      if (file.source == FileSourceKind.scan) file,
  ]..sort((a, b) => b.createdAt.compareTo(a.createdAt));

  if (scans.isNotEmpty && context.mounted) {
    await context.push(Routes.filePreview(scans.first.id), extra: scans.first);
  }
}

Future<PickedFile?> _photo(BuildContext context) async {
  final path = await context.push<String>(Routes.filesCamera);
  if (path == null) {
    return null;
  }

  return PickedFile(
    name: 'photo-${DateTime.now().millisecondsSinceEpoch}.jpg',
    path: path,
    readBytes: () => throw UnsupportedError('Camera photos have a path.'),
  );
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
