import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import 'file_models.dart';
import 'files_controller.dart';

/// A file's pages, as the backend renders them (plan section 2.0: the mobile
/// preview is the server's PDFium image of the same PDF that will be printed).
///
/// One page at a time, swiped; pinch to zoom. Pages load as they come into
/// view, at a width that suits the screen.
class FilePreviewScreen extends StatefulWidget {
  const FilePreviewScreen({super.key, required this.file});

  final UserFile file;

  @override
  State<FilePreviewScreen> createState() => _FilePreviewScreenState();
}

class _FilePreviewScreenState extends State<FilePreviewScreen> {
  int _current = 1;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final file = widget.file;

    return Scaffold(
      appBar: AppBar(
        title: Text(
          file.fileName,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
      ),
      body: Column(
        children: [
          Expanded(
            child: PageView.builder(
              itemCount: file.pageCount,
              onPageChanged: (index) => setState(() => _current = index + 1),
              itemBuilder: (context, index) => _PageImage(
                fileId: file.id,
                page: index + 1,
                total: file.pageCount,
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.all(Spacing.x3),
            child: Text(
              l10n.filesPageOf(_current, file.pageCount),
              style: Theme.of(context).textTheme.bodyMedium,
            ),
          ),
        ],
      ),
    );
  }
}

class _PageImage extends ConsumerStatefulWidget {
  const _PageImage({
    required this.fileId,
    required this.page,
    required this.total,
  });

  final String fileId;
  final int page;
  final int total;

  @override
  ConsumerState<_PageImage> createState() => _PageImageState();
}

class _PageImageState extends ConsumerState<_PageImage> {
  Future<Uint8List>? _image;
  int? _width;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();

    // Physical pixels, so the page is sharp; the server rounds to a few sizes.
    final media = MediaQuery.of(context);
    final width = (media.size.width * media.devicePixelRatio).round();

    if (width != _width) {
      _width = width;
      _load();
    }
  }

  void _load() {
    setState(() {
      _image = ref
          .read(filesRepositoryProvider)
          .pageImage(widget.fileId, widget.page, _width!);
    });
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final tokens = context.tokens;

    return FutureBuilder<Uint8List>(
      future: _image,
      builder: (context, snapshot) {
        if (snapshot.hasError) {
          return Center(
            child: Padding(
              padding: const EdgeInsets.all(Spacing.x6),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(Icons.broken_image_outlined, color: tokens.danger),
                  const SizedBox(height: Spacing.x2),
                  Text(l10n.filesPageFailed, textAlign: TextAlign.center),
                  const SizedBox(height: Spacing.x3),
                  OutlinedButton(
                    onPressed: _load,
                    child: Text(l10n.commonRetry),
                  ),
                ],
              ),
            ),
          );
        }

        if (!snapshot.hasData) {
          return Padding(
            padding: const EdgeInsets.all(Spacing.x4),
            child: Container(
              decoration: BoxDecoration(
                color: tokens.neutral100,
                borderRadius: BorderRadius.circular(Radii.sm),
              ),
            ),
          );
        }

        // On white in both themes: a page is paper, and a dark page behind a
        // white one reads as a broken image (docs/DESIGN_SYSTEM.md section 5.2).
        return InteractiveViewer(
          maxScale: 5,
          child: Center(
            child: Container(
              margin: const EdgeInsets.all(Spacing.x4),
              color: LightPalette.neutral0,
              child: Image.memory(
                snapshot.data!,
                fit: BoxFit.contain,
                semanticLabel: l10n.filesPageOf(widget.page, widget.total),
              ),
            ),
          ),
        );
      },
    );
  }
}
