import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/features/feature_flags.dart';
import '../../core/features/readiness.dart';
import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_design.dart';
import '../print_hub/print_next.dart';
import 'file_models.dart';
import 'files_controller.dart';

/// "Fayla baxış" (T-342, T-347; the design's screen 5): the pages as the
/// backend renders them (plan section 2.0: the server's PDFium image of the
/// same PDF that will be printed), what the file is, and the way on to its
/// print settings - the screen's one primary button.
///
/// Opened right after an upload too: until the file is ready it says it is
/// being prepared, and the pages appear when it is.
class FilePreviewScreen extends ConsumerStatefulWidget {
  const FilePreviewScreen({super.key, required this.file});

  final UserFile file;

  @override
  ConsumerState<FilePreviewScreen> createState() => _FilePreviewScreenState();
}

class _FilePreviewScreenState extends ConsumerState<FilePreviewScreen> {
  final _pages = PageController();
  int _current = 1;
  int _turns = 0;

  @override
  void dispose() {
    _pages.dispose();
    super.dispose();
  }

  /// The file as the list knows it now: its state changes as it converts.
  UserFile get _file =>
      ref
          .watch(filesControllerProvider)
          .files
          .where((file) => file.id == widget.file.id)
          .firstOrNull ??
      widget.file;

  Future<Uint8List> _load(int page, int width) =>
      ref.read(filesRepositoryProvider).pageImage(widget.file.id, page, width);

  void _goTo(int page) => _pages.animateToPage(
    page - 1,
    duration: Motion.base,
    curve: Motion.easing,
  );

  Future<void> _zoom(UserFile file) => Navigator.of(context).push(
    MaterialPageRoute<void>(
      fullscreenDialog: true,
      builder: (_) => Scaffold(
        appBar: AppBar(title: Text(file.fileName)),
        body: PagedPreview(
          pageCount: file.pageCount,
          initialPage: _current,
          quarterTurns: _turns,
          load: _load,
        ),
      ),
    ),
  );

  Future<void> _delete(UserFile file) async {
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

    if (confirmed != true || !mounted) {
      return;
    }

    final messenger = ScaffoldMessenger.of(context);
    final problem = await ref
        .read(filesControllerProvider.notifier)
        .delete(file);

    if (!mounted) {
      return;
    }

    messenger.showSnackBar(
      SnackBar(
        content: Text(
          problem == null ? l10n.filesDeleted : apiErrorText(l10n, problem),
        ),
      ),
    );
    if (problem == null) {
      context.pop();
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final file = _file;
    final next = ref.watch(printNextRouteProvider);
    final sharing =
        Readiness.fileSharing &&
        ref.watch(featureOnProvider(FeatureKey.fileSharing));
    final language = Localizations.localeOf(context).languageCode;

    final details = [
      if (file.pageCount > 0) l10n.filesPageCount(file.pageCount),
      formatBytes(file.sizeBytes, language: language),
      file.extension.toUpperCase(),
    ].join(' · ');

    return Scaffold(
      appBar: neoTabAppBar(
        context,
        title: l10n.filePreviewTitle,
        showBack: true,
      ),
      body: ListView(
        padding: const EdgeInsets.all(Spacing.x4),
        children: [
          // The page, on a soft grey stage, with "1 / N" under it.
          Container(
            height: MediaQuery.sizeOf(context).height * 0.42,
            decoration: BoxDecoration(
              color: tokens.neutral100,
              borderRadius: BorderRadius.circular(Radii.lg),
            ),
            clipBehavior: Clip.antiAlias,
            child: switch (file.status) {
              FileStatus.ready when file.pageCount > 0 => Stack(
                children: [
                  PageView.builder(
                    controller: _pages,
                    itemCount: file.pageCount,
                    onPageChanged: (index) =>
                        setState(() => _current = index + 1),
                    itemBuilder: (_, index) => _PageImage(
                      load: _load,
                      page: index + 1,
                      total: file.pageCount,
                      quarterTurns: _turns,
                    ),
                  ),
                  PositionedDirectional(
                    bottom: Spacing.x3,
                    start: 0,
                    end: 0,
                    child: Center(
                      child: Container(
                        padding: const EdgeInsets.symmetric(
                          horizontal: Spacing.x3,
                          vertical: Spacing.x1,
                        ),
                        decoration: BoxDecoration(
                          color: tokens.neutral0,
                          borderRadius: BorderRadius.circular(Radii.full),
                          border: Border.all(color: tokens.neutral200),
                        ),
                        child: Text(
                          l10n.filesPageOf(_current, file.pageCount),
                          style: theme.textTheme.labelMedium?.copyWith(
                            fontFeatures: const [FontFeature.tabularFigures()],
                          ),
                        ),
                      ),
                    ),
                  ),
                ],
              ),
              FileStatus.failed => Center(
                child: Padding(
                  padding: const EdgeInsets.all(Spacing.x4),
                  child: NeoBanner.error(
                    message: file.failureReason ?? l10n.filesStatusFailed,
                  ),
                ),
              ),
              _ => Center(
                child: Padding(
                  padding: const EdgeInsets.all(Spacing.x6),
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      const CircularProgressIndicator(),
                      const SizedBox(height: Spacing.x4),
                      Text(
                        l10n.filePreviewPreparing,
                        textAlign: TextAlign.center,
                        style: theme.textTheme.titleSmall,
                      ),
                      const SizedBox(height: Spacing.x1),
                      Text(
                        l10n.filePreviewPreparingHint,
                        textAlign: TextAlign.center,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: tokens.neutral600,
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            },
          ),
          const SizedBox(height: Spacing.x4),
          Text(
            file.fileName,
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
            style: theme.textTheme.titleLarge?.copyWith(
              fontWeight: FontWeights.semibold,
            ),
          ),
          const SizedBox(height: Spacing.x1),
          Text(
            details,
            style: theme.textTheme.bodySmall?.copyWith(
              color: tokens.neutral600,
            ),
          ),
          const SizedBox(height: Spacing.x4),
          Row(
            children: [
              _Action(
                icon: Icons.zoom_in,
                label: l10n.filePreviewZoom,
                onTap: file.canPreview ? () => _zoom(file) : null,
              ),
              _Action(
                icon: Icons.rotate_right,
                label: l10n.filePreviewRotate,
                onTap: file.canPreview
                    ? () => setState(() => _turns = (_turns + 1) % 4)
                    : null,
              ),
              if (sharing)
                _Action(
                  icon: Icons.share_outlined,
                  label: l10n.filePreviewShare,
                  onTap: file.status == FileStatus.ready
                      ? () => context.push(Routes.shareSend, extra: file)
                      : null,
                ),
              _Action(
                icon: Icons.delete_outline,
                label: l10n.commonDelete,
                danger: true,
                onTap: () => _delete(file),
              ),
            ],
          ),
          if (next != null) ...[
            const SizedBox(height: Spacing.x4),
            FilledButton.icon(
              onPressed: canPrint(file)
                  ? () => context.push(next, extra: file)
                  : null,
              iconAlignment: IconAlignment.end,
              icon: const Icon(Icons.arrow_forward),
              label: Text(l10n.filePreviewPrintSettings),
            ),
          ],
          if (file.canPreview && file.pageCount > 1) ...[
            const SizedBox(height: Spacing.x4),
            SizedBox(
              height: 132,
              child: ListView.separated(
                scrollDirection: Axis.horizontal,
                itemCount: file.pageCount,
                separatorBuilder: (_, __) => const SizedBox(width: Spacing.x2),
                itemBuilder: (_, index) => _Thumbnail(
                  load: _load,
                  page: index + 1,
                  total: file.pageCount,
                  selected: index + 1 == _current,
                  onTap: () => _goTo(index + 1),
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

/// One of the round actions under the file's name.
class _Action extends StatelessWidget {
  const _Action({
    required this.icon,
    required this.label,
    required this.onTap,
    this.danger = false,
  });

  final IconData icon;
  final String label;
  final VoidCallback? onTap;
  final bool danger;

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;
    final colour = onTap == null
        ? tokens.neutral400
        : danger
        ? tokens.danger
        : tokens.neutral800;

    return Expanded(
      child: InkWell(
        borderRadius: BorderRadius.circular(Radii.md),
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: Spacing.x2),
          child: Column(
            children: [
              Icon(icon, color: colour),
              const SizedBox(height: Spacing.x1),
              Text(
                label,
                textAlign: TextAlign.center,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: Theme.of(context).textTheme.labelSmall
                    ?.copyWith(color: colour),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// A small page under the preview; the one shown is outlined.
class _Thumbnail extends StatefulWidget {
  const _Thumbnail({
    required this.load,
    required this.page,
    required this.total,
    required this.selected,
    required this.onTap,
  });

  final PageLoader load;
  final int page;
  final int total;
  final bool selected;
  final VoidCallback onTap;

  @override
  State<_Thumbnail> createState() => _ThumbnailState();
}

class _ThumbnailState extends State<_Thumbnail> {
  // The server's smallest size; it rounds to a few widths anyway.
  late final Future<Uint8List> _image = widget.load(widget.page, 320);

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;
    final theme = Theme.of(context);

    return Semantics(
      button: true,
      selected: widget.selected,
      label: AppLocalizations.of(context)
          .filesPageOf(widget.page, widget.total),
      child: InkWell(
        onTap: widget.onTap,
        borderRadius: BorderRadius.circular(Radii.sm),
        child: Column(
          children: [
            Container(
              width: 72,
              height: 96,
              decoration: BoxDecoration(
                color: LightPalette.neutral0,
                borderRadius: BorderRadius.circular(Radii.sm),
                border: Border.all(
                  color: widget.selected ? tokens.brand500 : tokens.neutral200,
                  width: widget.selected ? 2 : 1,
                ),
              ),
              clipBehavior: Clip.antiAlias,
              child: FutureBuilder<Uint8List>(
                future: _image,
                builder: (_, snapshot) => snapshot.hasData
                    ? Image.memory(snapshot.data!, fit: BoxFit.contain)
                    : const SizedBox.shrink(),
              ),
            ),
            const SizedBox(height: Spacing.x1),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: Spacing.x2),
              decoration: BoxDecoration(
                color: widget.selected ? tokens.brand500 : null,
                borderRadius: BorderRadius.circular(Radii.full),
              ),
              child: Text(
                '${widget.page}',
                style: theme.textTheme.labelSmall?.copyWith(
                  color: widget.selected ? tokens.neutral0 : tokens.neutral600,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// Loads one page at the width asked for, as a PNG.
typedef PageLoader = Future<Uint8List> Function(int page, int width);

/// Pages swiped one at a time with "1 / N" under them: a file's zoomed
/// view, or a shared file's before it is saved (docs/DESIGN_SYSTEM.md,
/// "Mobil: fayl paylaşımı").
class PagedPreview extends StatefulWidget {
  const PagedPreview({
    super.key,
    required this.pageCount,
    required this.load,
    this.initialPage = 1,
    this.quarterTurns = 0,
  });

  final int pageCount;
  final PageLoader load;
  final int initialPage;
  final int quarterTurns;

  @override
  State<PagedPreview> createState() => _PagedPreviewState();
}

class _PagedPreviewState extends State<PagedPreview> {
  late int _current = widget.initialPage;
  late final _controller = PageController(initialPage: widget.initialPage - 1);

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    return Column(
      children: [
        Expanded(
          child: PageView.builder(
            controller: _controller,
            itemCount: widget.pageCount,
            onPageChanged: (index) => setState(() => _current = index + 1),
            itemBuilder: (context, index) => _PageImage(
              load: widget.load,
              page: index + 1,
              total: widget.pageCount,
              quarterTurns: widget.quarterTurns,
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
    this.quarterTurns = 0,
  });

  final PageLoader load;
  final int page;
  final int total;
  final int quarterTurns;

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
            child: RotatedBox(
              quarterTurns: widget.quarterTurns,
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
          ),
        );
      },
    );
  }
}
