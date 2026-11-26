import 'package:file_picker/file_picker.dart';
import 'package:flutter/foundation.dart';
import 'package:material_ui/material_ui.dart';
import 'package:flutter/services.dart';
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
import 'cloud_picker.dart';
import 'files_controller.dart';

/// The ways a file gets into "Fayllarım": the files tab's upload sheet and
/// the Print tab both start here (docs/DESIGN_SYSTEM.md, "Mobil: yeni
/// vizual dil").
enum FileSource {
  /// The system picker, for a file on the phone.
  device,

  /// A cloud app's own chooser - Google Drive, OneDrive, Dropbox (T-399,
  /// docs/DECISIONS.md 2026-11-10). Not on the web.
  cloud,
  camera,
  scan,
  layout,
}

/// The ways in, as a bottom sheet: the files tab and "Fayllarımdan seç" in
/// the Print tab (T-353) offer the same ones. Null when nothing was chosen.
///
/// [forLayout]: the layout's file choice (T-450) - only the ways that bring
/// a file a layout can use: no scan (a saved scan is locked) and no new
/// layout inside a layout.
Future<FileSource?> chooseFileSource(
  BuildContext context, {
  bool forLayout = false,
}) async {
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
            // The cloud app's own chooser, not the phone's files (T-399).
            if (!kIsWeb)
              ListTile(
                leading: const Icon(Icons.cloud_upload_outlined),
                title: Text(l10n.filesFromCloud),
                subtitle: Text(l10n.filesFromCloudHint),
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
            if (!forLayout &&
                Readiness.documentScan &&
                DocumentScanner.isSupportedPlatform)
              ListTile(
                leading: const Icon(Icons.document_scanner_outlined),
                title: Text(l10n.filesScanDocument),
                subtitle: Text(l10n.filesScanDocumentHint),
                onTap: () => Navigator.pop(context, FileSource.scan),
              ),
            // Pictures and PDF pages on one sheet (plan section 2.11).
            if (!forLayout)
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

/// The most files "Fayl seç" uploads at once (T-464).
const maxFilesAtOnce = 20;

/// How many of them upload at the same time; the rest wait their turn.
const parallelUploads = 3;

/// Adds files from [source]. From the files tab, the files uploaded from the
/// phone (several at once, T-464), a cloud app or the camera are returned
/// (the layout's file choice selects them when they are ready, T-450);
/// nothing otherwise.
///
/// From the files tab ([thenPrint] false) the upload runs in
/// [filesControllerProvider] and shows on the list. From the Print tab
/// ([thenPrint] true) the user is printing (T-347): the chosen file uploads
/// on its own screen, which opens the file's preview - and from there its
/// print settings - once it is ready; a saved scan opens its preview at once.
///
/// Several photos from the camera (T-466) are asked about on the files tab -
/// one PDF, a layout, or separate pictures; for a layout ([forLayout]) they
/// are uploaded and returned.
Future<List<UserFile>> addFileFrom(
  BuildContext context,
  WidgetRef ref,
  FileSource source, {
  bool thenPrint = false,
  bool forLayout = false,
}) async {
  final l10n = AppLocalizations.of(context);
  final controller = ref.read(filesControllerProvider.notifier);

  Future<List<UserFile>> send(PickedFile picked) async {
    if (thenPrint) {
      await context.push(Routes.printUpload, extra: picked);
      return const [];
    }
    final file = await controller.upload(picked);
    return [?file];
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
        return const [];
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
          return send(photo);
        }
      }
    case FileSource.device:
      // Printing goes on with one file; the files tab takes several.
      if (thenPrint) {
        final picked = await _pickFiles(ref, multiple: false);
        if (picked.isNotEmpty && context.mounted) {
          return send(picked.first);
        }
        break;
      }
      final picked = await _pickFiles(ref, multiple: true);
      if (picked.isEmpty || !context.mounted) {
        break;
      }
      if (picked.length > maxFilesAtOnce) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(l10n.filesTooManyAtOnce(maxFilesAtOnce))),
        );
      }
      return controller.uploadAll(
        picked.take(maxFilesAtOnce).toList(),
        parallel: parallelUploads,
      );
    case FileSource.cloud:
      final picked = await _pickCloud(context, ref);
      if (picked != null && context.mounted) {
        return send(picked);
      }
    case FileSource.camera:
      // Printing goes on with one photo.
      if (thenPrint) {
        final photo = await _photo(context);
        if (photo != null && context.mounted) {
          return send(photo);
        }
        break;
      }
      final photos = await _photos(context);
      if (photos.isEmpty || !context.mounted) {
        break;
      }
      if (photos.length == 1 || forLayout) {
        return controller.uploadAll(photos, parallel: parallelUploads);
      }
      final keep = await _askPhotosAs(context, photos.length);
      if (!context.mounted) {
        break;
      }
      final uploaded = await controller.uploadAll(
        photos,
        parallel: parallelUploads,
      );
      if (!context.mounted || uploaded.isEmpty) {
        break;
      }
      switch (keep) {
        case PhotosAs.separate:
          return uploaded;
        case PhotosAs.pdf:
          await savePhotosAsPdf(context, ref, uploaded);
        case PhotosAs.layout:
          await openPhotosInLayout(context, ref, uploaded);
      }
  }
  return const [];
}

/// What several photos become (T-466).
enum PhotosAs { pdf, layout, separate }

/// "N şəkli necə saxlayaq?": answered, never dismissed - the photos are
/// taken and would be lost.
Future<PhotosAs> _askPhotosAs(BuildContext context, int count) async {
  final l10n = AppLocalizations.of(context);
  final choice = await showModalBottomSheet<PhotosAs>(
    context: context,
    isDismissible: false,
    enableDrag: false,
    showDragHandle: true,
    builder: (context) => SafeArea(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(
              Spacing.x4,
              0,
              Spacing.x4,
              Spacing.x2,
            ),
            child: Text(
              l10n.photosAsTitle(count),
              style: Theme.of(context).textTheme.titleMedium,
            ),
          ),
          ListTile(
            key: const ValueKey('photosAsPdf'),
            leading: const Icon(Icons.picture_as_pdf_outlined),
            title: Text(l10n.photosAsPdf),
            subtitle: Text(l10n.photosAsPdfHint),
            onTap: () => Navigator.pop(context, PhotosAs.pdf),
          ),
          ListTile(
            key: const ValueKey('photosAsLayout'),
            leading: const Icon(Icons.dashboard_customize_outlined),
            title: Text(l10n.photosAsLayout),
            subtitle: Text(l10n.photosAsLayoutHint),
            onTap: () => Navigator.pop(context, PhotosAs.layout),
          ),
          ListTile(
            key: const ValueKey('photosAsSeparate'),
            leading: const Icon(Icons.photo_library_outlined),
            title: Text(l10n.photosAsSeparate),
            onTap: () => Navigator.pop(context, PhotosAs.separate),
          ),
          const SizedBox(height: Spacing.x4),
        ],
      ),
    ),
  );
  return choice ?? PhotosAs.separate;
}

/// Several photos from the camera (T-466); none when the user went back.
Future<List<PickedFile>> _photos(BuildContext context) async {
  final paths = await context.push<List<String>>(
    '${Routes.filesCamera}?multiple=1',
  );
  final stamp = DateTime.now().millisecondsSinceEpoch;
  return [
    for (final (i, path) in (paths ?? const <String>[]).indexed)
      PickedFile(
        name: 'photo-$stamp-${i + 1}.jpg',
        path: path,
        readBytes: () => throw UnsupportedError('Camera photos have a path.'),
      ),
  ];
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

/// "Buluddan əlavə et" (T-399): the one cloud app on the phone opens at
/// once, several are offered first; with none, the system picker - and why.
Future<PickedFile?> _pickCloud(BuildContext context, WidgetRef ref) async {
  final l10n = AppLocalizations.of(context);
  final messenger = ScaffoldMessenger.of(context);
  final picker = ref.read(cloudPickerProvider);

  List<CloudApp> apps;
  try {
    apps = await picker.installed();
  } on PlatformException {
    apps = const [];
  }
  if (!context.mounted) {
    return null;
  }

  if (apps.isEmpty) {
    messenger.showSnackBar(SnackBar(content: Text(l10n.filesNoCloudApp)));
    return (await _pickFiles(ref, multiple: false)).firstOrNull;
  }

  final app = apps.length == 1
      ? apps.single
      : await showModalBottomSheet<CloudApp>(
          context: context,
          showDragHandle: true,
          builder: (context) => SafeArea(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Padding(
                  padding: const EdgeInsets.fromLTRB(
                    Spacing.x4,
                    0,
                    Spacing.x4,
                    Spacing.x2,
                  ),
                  child: Text(
                    l10n.filesWhichCloud,
                    style: Theme.of(context).textTheme.titleMedium,
                  ),
                ),
                for (final app in apps)
                  ListTile(
                    leading: const Icon(Icons.cloud_outlined),
                    title: Text(app.title),
                    onTap: () => Navigator.pop(context, app),
                  ),
              ],
            ),
          ),
        );
  if (app == null) {
    return null;
  }

  try {
    final file = await picker.pick(app);
    if (file == null) {
      return null;
    }
    return PickedFile(
      name: file.name,
      path: file.path,
      readBytes: () => throw UnsupportedError('A cloud copy has a path.'),
    );
  } on CloudPickException catch (error) {
    messenger.showSnackBar(
      SnackBar(
        content: Text(
          error.failure == CloudPickFailure.tooLarge
              ? l10n.filesCloudTooLarge
              : l10n.filesCloudFailed(app.title),
        ),
      ),
    );
    return null;
  }
}

/// The system picker, filtered to the formats the installation accepts
/// now - plus HEIC, which the phone converts before upload. Several files
/// when [multiple] (T-464); nothing when the user chose none.
Future<List<PickedFile>> _pickFiles(
  WidgetRef ref, {
  required bool multiple,
}) async {
  Set<String>? accepted;

  try {
    accepted = await ref.read(filesRepositoryProvider).acceptedFormats();
  } on Exception {
    // Without the list, pick anything: the server still checks the format.
    accepted = null;
  }

  final type = accepted == null ? FileType.any : FileType.custom;
  final extensions = accepted == null
      ? null
      : [...accepted, ...phoneConvertedExtensions];

  final files = multiple
      ? await FilePicker.pickFiles(type: type, allowedExtensions: extensions)
      : [?await FilePicker.pickFile(type: type, allowedExtensions: extensions)];

  return [
    for (final file in files)
      PickedFile(
        name: file.name,
        path: kIsWeb ? null : file.path,
        readBytes: file.xFile.readAsBytes,
      ),
  ];
}
