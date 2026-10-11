import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_client.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
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
  @override
  Widget build(BuildContext context) {
    final file = widget.file;

    return Scaffold(
      appBar: AppBar(
        title: Text(
          file.fileName,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
      ),
      body: Consumer(
        builder: (context, ref, _) => PagedPreview(
          pageCount: file.pageCount,
          load: (page, width) =>
              ref.read(filesRepositoryProvider).pageImage(file.id, page, width),
        ),
      ),
    );
  }
}

/// Loads one page at the width asked for, as a PNG.
typedef PageLoader = Future<Uint8List> Function(int page, int width);

/// Pages swiped one at a time with "1 / N" under them: a file's, or a
/// shared file's before it is saved (docs/DESIGN_SYSTEM.md, "Mobil: fayl
/// paylaşımı").
class PagedPreview extends StatefulWidget {
  const PagedPreview({super.key, required this.pageCount, required this.load});

  final int pageCount;
  final PageLoader load;

  @override
  State<PagedPreview> createState() => _PagedPreviewState();
}

class _PagedPreviewState extends State<PagedPreview> {
  int _current = 1;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    return Column(
      children: [
        Expanded(
          child: PageView.builder(
            itemCount: widget.pageCount,
            onPageChanged: (index) => setState(() => _current = index + 1),
            itemBuilder: (context, index) => _PageImage(
              load: widget.load,
              page: index + 1,
              total: widget.pageCount,
            ),
          ),
        ),
        Padding(
          padding: const EdgeInsets.all(Spacing.x3),
          child: Text(
            l10n.filesPageOf(_current, widget.pageCount),
            style: Theme.of(context).textTheme.bodyMedium,
          ),
        ),
      ],
    );
  }
}

class _PageImage extends StatefulWidget {
  const _PageImage({
    required this.load,
    required this.page,
    required this.total,
  });

  final PageLoader load;
  final int page;
  final int total;

  @override
  State<_PageImage> createState() => _PageImageState();
}

class _PageImageState extends State<_PageImage> {
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
      _image = widget.load(widget.page, _width!);
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
          // The server's own sentence when it sent one - "the document
          // service is not running, try again shortly" tells more than "the
          // page could not be shown" - and no connection in the app's words.
          final error = snapshot.error;
          final reason =
              error is ApiException &&
                  (error.kind != ApiErrorKind.server ||
                      error.message.isNotEmpty)
              ? apiErrorText(l10n, error)
              : l10n.filesPageFailed;

          return Center(
            child: Padding(
              padding: const EdgeInsets.all(Spacing.x6),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(Icons.broken_image_outlined, color: tokens.danger),
                  const SizedBox(height: Spacing.x2),
                  Text(reason, textAlign: TextAlign.center),
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
