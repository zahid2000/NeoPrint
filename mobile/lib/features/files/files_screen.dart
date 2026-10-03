import '../../shared/api_error_text.dart';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/features/readiness.dart';
import '../../core/format/formats.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../../shared/widgets/neo_status_chip.dart';
import 'file_models.dart';
import 'files_controller.dart';
import 'heic_converter.dart';

/// "Fayllarım" (plan sections 2.2, 8.2): upload from the phone or the camera,
/// the list with where each file came from, preview and delete.
class FilesScreen extends ConsumerWidget {
  const FilesScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final state = ref.watch(filesControllerProvider);
    final controller = ref.read(filesControllerProvider.notifier);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.navFiles)),
      // The screen's one primary action (docs/DESIGN_SYSTEM.md section 4).
      floatingActionButton: state.files.isEmpty && state.uploads.isEmpty
          ? null
          : FloatingActionButton.extended(
              onPressed: () => _chooseSource(context, ref),
              icon: const Icon(Icons.upload_file),
              label: Text(l10n.filesUpload),
            ),
      body: RefreshIndicator(
        onRefresh: controller.load,
        child: _body(context, ref, state),
      ),
    );
  }

  Widget _body(BuildContext context, WidgetRef ref, FilesState state) {
    final l10n = AppLocalizations.of(context);

    if (state.loading) {
      return const _SkeletonList();
    }

    if (state.error != null && state.files.isEmpty) {
      return ListView(
        padding: const EdgeInsets.all(Spacing.x4),
        children: [
          NeoBanner.error(
            message:
                '${l10n.filesLoadFailed} ${apiErrorText(l10n, state.error!)}',
          ),
          const SizedBox(height: Spacing.x4),
          OutlinedButton(
            onPressed: ref.read(filesControllerProvider.notifier).load,
            child: Text(l10n.commonRetry),
          ),
        ],
      );
    }

    if (state.files.isEmpty && state.uploads.isEmpty) {
      // Scrollable, so pull-to-refresh still works on an empty list.
      return ListView(
        children: [
          NeoEmptyState(
            icon: Icons.folder_open_outlined,
            message: l10n.filesEmpty,
            hint: l10n.filesEmptyHint,
            action: FilledButton.icon(
              onPressed: () => _chooseSource(context, ref),
              icon: const Icon(Icons.upload_file),
              label: Text(l10n.filesUpload),
            ),
          ),
        ],
      );
    }

    return ListView(
      padding: const EdgeInsets.fromLTRB(
        Spacing.x4,
        Spacing.x4,
        Spacing.x4,
        // Room for the floating button over the last row.
        Spacing.x16 + Spacing.x8,
      ),
      children: [
        for (final upload in state.uploads) ...[
          _UploadCard(upload: upload),
          const SizedBox(height: Spacing.x3),
        ],
        for (final file in state.files) ...[
          _FileCard(file: file),
          const SizedBox(height: Spacing.x3),
        ],
      ],
    );
  }

  Future<void> _chooseSource(BuildContext context, WidgetRef ref) async {
    final l10n = AppLocalizations.of(context);

    final choice = await showModalBottomSheet<_Source>(
      context: context,
      showDragHandle: true,
      builder: (context) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            ListTile(
              leading: const Icon(Icons.folder_open_outlined),
              title: Text(l10n.filesPickFile),
              subtitle: Text(l10n.filesPickFileHint),
              onTap: () => Navigator.pop(context, _Source.file),
            ),
            if (!kIsWeb)
              ListTile(
                leading: const Icon(Icons.photo_camera_outlined),
                title: Text(l10n.filesTakePhoto),
                subtitle: Text(l10n.filesTakePhotoHint),
                onTap: () => Navigator.pop(context, _Source.camera),
              ),
            const SizedBox(height: Spacing.x4),
          ],
        ),
      ),
    );

    if (choice == null || !context.mounted) {
      return;
    }

    final controller = ref.read(filesControllerProvider.notifier);

    switch (choice) {
      case _Source.file:
        final picked = await _pickFile(ref);
        if (picked != null) {
          await controller.upload(picked);
        }
      case _Source.camera:
        final path = await context.push<String>(Routes.filesCamera);
        if (path != null) {
          await controller.upload(
            PickedFile(
              name: 'photo-${DateTime.now().millisecondsSinceEpoch}.jpg',
              path: path,
              readBytes: () =>
                  throw UnsupportedError('Camera photos have a path.'),
            ),
          );
        }
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
}

enum _Source { file, camera }

/// A file on the list: name, what it is, where it came from, and its state.
class _FileCard extends ConsumerWidget {
  const _FileCard({required this.file});

  final UserFile file;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final text = Theme.of(context).textTheme;
    final tokens = context.tokens;

    final details = [
      formatBytes(
        file.sizeBytes,
        language: Localizations.localeOf(context).languageCode,
      ),
      if (file.pageCount > 0) l10n.filesPageCount(file.pageCount),
      formatDateTime(
        file.createdAt,
        today: l10n.commonToday,
        yesterday: l10n.commonYesterday,
      ),
    ].join(' · ');

    return Card(
      margin: EdgeInsets.zero,
      child: InkWell(
        borderRadius: BorderRadius.circular(Radii.md),
        onTap: () => _open(context),
        child: Padding(
          padding: const EdgeInsets.all(Spacing.x4),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Icon(_iconFor(file.extension), color: tokens.brand500),
              const SizedBox(width: Spacing.x3),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      file.fileName,
                      style: text.titleMedium,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                    ),
                    const SizedBox(height: Spacing.x1),
                    Text(
                      details,
                      style: text.bodySmall?.copyWith(color: tokens.neutral600),
                    ),
                    const SizedBox(height: Spacing.x2),
                    Wrap(
                      spacing: Spacing.x2,
                      runSpacing: Spacing.x2,
                      children: [
                        NeoStatusChip(
                          label: _sourceLabel(l10n, file.source),
                          tone: StatusTone.neutral,
                          icon: _sourceIcon(file.source),
                        ),
                        if (file.isSensitive)
                          NeoStatusChip(
                            label: l10n.filesSensitive,
                            tone: StatusTone.warning,
                            icon: Icons.lock_outline,
                          ),
                        if (file.status != FileStatus.ready)
                          NeoStatusChip(
                            label: switch (file.status) {
                              FileStatus.pending => l10n.filesStatusPending,
                              FileStatus.converting =>
                                l10n.filesStatusConverting,
                              _ => l10n.filesStatusFailed,
                            },
                            tone: file.status == FileStatus.failed
                                ? StatusTone.danger
                                : StatusTone.info,
                          ),
                      ],
                    ),
                    if (file.status == FileStatus.failed &&
                        file.failureReason != null) ...[
                      const SizedBox(height: Spacing.x2),
                      Text(
                        file.failureReason!,
                        style: text.bodySmall?.copyWith(color: tokens.danger),
                      ),
                    ],
                  ],
                ),
              ),
              PopupMenuButton<String>(
                tooltip: l10n.commonMore,
                onSelected: (choice) => choice == 'prepare'
                    ? context.push(Routes.prepare, extra: file)
                    : _delete(context, ref),
                itemBuilder: (_) => [
                  if (Readiness.personalQr && file.status == FileStatus.ready)
                    PopupMenuItem(
                      value: 'prepare',
                      child: ListTile(
                        leading: Icon(
                          Icons.storefront_outlined,
                          color: tokens.brand500,
                        ),
                        title: Text(l10n.prepareMenu),
                        contentPadding: EdgeInsets.zero,
                      ),
                    ),
                  PopupMenuItem(
                    value: 'delete',
                    child: ListTile(
                      leading: Icon(Icons.delete_outline, color: tokens.danger),
                      title: Text(l10n.commonDelete),
                      contentPadding: EdgeInsets.zero,
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }

  void _open(BuildContext context) {
    if (file.canPreview) {
      context.push(Routes.filePreview(file.id), extra: file);
      return;
    }

    if (!file.status.isSettled) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(AppLocalizations.of(context).filesNotReady)),
      );
    }
  }

  Future<void> _delete(BuildContext context, WidgetRef ref) async {
    final l10n = AppLocalizations.of(context);
    final tokens = context.tokens;

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(l10n.filesDeleteTitle(file.fileName)),
        content: Text(l10n.filesDeleteExplanation),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: Text(l10n.commonCancel),
          ),
          FilledButton(
            style: FilledButton.styleFrom(
              backgroundColor: tokens.danger,
              foregroundColor: tokens.neutral0,
            ),
            onPressed: () => Navigator.pop(context, true),
            child: Text(l10n.commonDelete),
          ),
        ],
      ),
    );

    if (confirmed != true || !context.mounted) {
      return;
    }

    final problem = await ref
        .read(filesControllerProvider.notifier)
        .delete(file);

    if (context.mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(
            problem == null ? l10n.filesDeleted : apiErrorText(l10n, problem),
          ),
        ),
      );
    }
  }

  static IconData _iconFor(String extension) => switch (extension) {
    'pdf' => Icons.picture_as_pdf_outlined,
    'jpg' ||
    'jpeg' ||
    'png' ||
    'bmp' ||
    'gif' ||
    'webp' => Icons.image_outlined,
    'xls' ||
    'xlsx' ||
    'xlsm' ||
    'xltx' ||
    'ods' ||
    'csv' => Icons.table_chart_outlined,
    'ppt' ||
    'pptx' ||
    'pps' ||
    'ppsx' ||
    'potx' ||
    'odp' => Icons.slideshow_outlined,
    _ => Icons.description_outlined,
  };

  static String _sourceLabel(AppLocalizations l10n, FileSourceKind source) =>
      switch (source) {
        FileSourceKind.uploaded => l10n.filesSourceUploaded,
        FileSourceKind.scan => l10n.filesSourceScan,
        FileSourceKind.layout => l10n.filesSourceLayout,
        FileSourceKind.usb => l10n.filesSourceUsb,
        FileSourceKind.shared => l10n.filesSourceShared,
      };

  static IconData _sourceIcon(FileSourceKind source) => switch (source) {
    FileSourceKind.uploaded => Icons.upload_outlined,
    FileSourceKind.scan => Icons.document_scanner_outlined,
    FileSourceKind.layout => Icons.dashboard_customize_outlined,
    FileSourceKind.usb => Icons.usb,
    FileSourceKind.shared => Icons.share_outlined,
  };
}

/// An upload on its way, or one that failed with the reason and a way out.
class _UploadCard extends ConsumerWidget {
  const _UploadCard({required this.upload});

  final UploadItem upload;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final text = Theme.of(context).textTheme;
    final controller = ref.read(filesControllerProvider.notifier);
    final error = upload.error;

    if (error != null) {
      final message = switch (error) {
        ServerUploadError(:final error) => apiErrorText(l10n, error),
        HeicUploadError(:final reason) =>
          reason == HeicFailure.unsupported
              ? l10n.filesHeicUnsupported
              : l10n.filesHeicFailed,
        NetworkUploadError() => l10n.filesUploadNetwork,
      };

      return NeoBanner.error(
        message: '${l10n.filesUploadFailed(upload.fileName)}. $message',
        onClose: () => controller.dismissUpload(upload.id),
      );
    }

    final percent = (upload.progress * 100).round();

    return Card(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    upload.fileName,
                    style: text.titleMedium,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
                IconButton(
                  tooltip: l10n.filesCancelUpload,
                  onPressed: () => controller.cancelUpload(upload.id),
                  icon: const Icon(Icons.close),
                ),
              ],
            ),
            const SizedBox(height: Spacing.x2),
            LinearProgressIndicator(
              value: upload.progress == 0 ? null : upload.progress,
            ),
            const SizedBox(height: Spacing.x2),
            Text(
              upload.progress == 0
                  ? l10n.filesPreparingPhoto
                  : l10n.filesUploading(percent),
              style: text.bodySmall,
            ),
          ],
        ),
      ),
    );
  }
}

/// The list while it loads: the shape of the rows, not a spinner
/// (docs/DESIGN_SYSTEM.md section 4, "Yüklənmə").
class _SkeletonList extends StatelessWidget {
  const _SkeletonList();

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;

    return ListView.separated(
      padding: const EdgeInsets.all(Spacing.x4),
      itemCount: 4,
      separatorBuilder: (_, _) => const SizedBox(height: Spacing.x3),
      itemBuilder: (_, _) => Container(
        height: Spacing.x16 + Spacing.x8,
        decoration: BoxDecoration(
          color: tokens.neutral100,
          borderRadius: BorderRadius.circular(Radii.md),
        ),
      ),
    );
  }
}
