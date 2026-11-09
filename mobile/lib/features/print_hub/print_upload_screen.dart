import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_design.dart';
import '../files/file_models.dart';
import '../files/files_controller.dart';
import '../files/heic_converter.dart';
import 'print_next.dart';
import 'print_start.dart';

/// A file chosen in the Print tab, on its way up (T-347): the progress here,
/// then - once the server has its pages - straight on to its print settings
/// (T-353), not to the files tab, where the user would have to find it again.
class PrintUploadScreen extends ConsumerStatefulWidget {
  const PrintUploadScreen({super.key, required this.file});

  final PickedFile file;

  @override
  ConsumerState<PrintUploadScreen> createState() => _PrintUploadScreenState();
}

class _PrintUploadScreenState extends ConsumerState<PrintUploadScreen> {
  @override
  void initState() {
    super.initState();
    // After the first frame: the upload changes the files' state, which may
    // not happen while the tree is being built.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) {
        unawaited(_upload());
      }
    });
  }

  /// The uploaded file, while the server turns it into pages.
  String? _savedId;
  bool _moved = false;

  Future<void> _upload() async {
    final saved = await ref
        .read(filesControllerProvider.notifier)
        .upload(widget.file);

    if (saved != null && mounted) {
      setState(() => _savedId = saved.id);
      _onSaved(saved);
    }
  }

  /// T-356: "Çap et" or "Kiosk üçün hazırla", as from a file's menu; if
  /// that is not chosen, the file's preview.
  Future<void> _goOn(UserFile file) async {
    if (canPrint(file) && await startPrint(context, ref, file, replace: true)) {
      return;
    }
    if (mounted) {
      context.pushReplacement(Routes.filePreview(file.id), extra: file);
    }
  }

  /// T-353: once the file is ready its print settings open straight away;
  /// a file that could not be opened, or no way to print on, shows its
  /// preview, which says why.
  void _onSaved(UserFile file) {
    if (_moved ||
        file.status == FileStatus.pending ||
        file.status == FileStatus.converting) {
      return;
    }

    _moved = true;
    unawaited(_goOn(file));
  }

  UploadItem? _item(FilesState state) => state.uploads
      .where((upload) => upload.fileName == widget.file.name)
      .lastOrNull;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final files = ref.watch(filesControllerProvider);
    final item = _item(files);
    final error = _savedId == null ? item?.error : null;
    final saved = files.files.where((f) => f.id == _savedId).firstOrNull;
    if (saved != null && !_moved) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) {
          _onSaved(saved);
        }
      });
    }

    return Scaffold(
      appBar: neoTabAppBar(context, title: l10n.printHubTitle, showBack: true),
      body: ListView(
        padding: const EdgeInsets.all(Spacing.x4),
        children: [
          Card(
            child: Padding(
              padding: const EdgeInsets.all(Spacing.x4),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Row(
                    children: [
                      FileTypeTile(extension: extensionOf(widget.file.name)),
                      const SizedBox(width: Spacing.x3),
                      Expanded(
                        child: Text(
                          widget.file.name,
                          maxLines: 2,
                          overflow: TextOverflow.ellipsis,
                          style: theme.textTheme.titleMedium,
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: Spacing.x4),
                  if (error == null) ...[
                    LinearProgressIndicator(
                      value: _savedId != null || (item?.progress ?? 0) == 0
                          ? null
                          : item!.progress,
                    ),
                    const SizedBox(height: Spacing.x2),
                    Text(
                      _savedId != null
                          ? l10n.filesStatusConverting
                          : (item?.progress ?? 0) == 0
                          ? l10n.filesPreparingPhoto
                          : l10n.filesUploading(
                              ((item?.progress ?? 0) * 100).round(),
                            ),
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: tokens.neutral600,
                      ),
                    ),
                    const SizedBox(height: Spacing.x2),
                    Text(
                      l10n.printUploadThen,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: tokens.neutral600,
                      ),
                    ),
                  ] else ...[
                    NeoBanner.error(
                      message:
                          '${l10n.filesUploadFailed(widget.file.name)}. '
                          '${switch (error) {
                            ServerUploadError(:final error) => apiErrorText(l10n, error),
                            HeicUploadError(:final reason) => reason == HeicFailure.unsupported ? l10n.filesHeicUnsupported : l10n.filesHeicFailed,
                            NetworkUploadError() => l10n.filesUploadNetwork,
                          }}',
                    ),
                    const SizedBox(height: Spacing.x3),
                    FilledButton(
                      onPressed: () {
                        ref
                            .read(filesControllerProvider.notifier)
                            .dismissUpload(item!.id);
                        unawaited(_upload());
                      },
                      child: Text(l10n.commonRetry),
                    ),
                  ],
                ],
              ),
            ),
          ),
          if (error == null && item != null && _savedId == null) ...[
            const SizedBox(height: Spacing.x3),
            TextButton(
              onPressed: () {
                ref
                    .read(filesControllerProvider.notifier)
                    .cancelUpload(item.id);
                context.pop();
              },
              child: Text(l10n.filesCancelUpload),
            ),
          ],
        ],
      ),
    );
  }
}
