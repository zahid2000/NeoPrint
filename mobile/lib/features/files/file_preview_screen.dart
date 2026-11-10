import 'dart:async';
import 'dart:collection';
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
import '../print_hub/print_start.dart';
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

  /// Two pages at a time (T-405): the page on the stage asks first, the
  /// thumbnails wait their turn instead of all rendering at once.
  final _queue = PageQueue();

  Future<Uint8List> _load(int page, int width) => _queue.run(
    () => ref
        .read(filesRepositoryProvider)
        .pageImage(widget.file.id, page, width),
  );

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
                  ? () => startPrint(context, ref, file)
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

/// At most [slots] page loads at once, in the order asked (T-405).
final class PageQueue {
  PageQueue({this.slots = 2});

  final int slots;
  int _running = 0;
  final _waiting = Queue<Completer<void>>();

  Future<T> run<T>(Future<T> Function() task) async {
    if (_running >= slots) {
      final turn = Completer<void>();
      _waiting.add(turn);
      await turn.future;
    }
    _running++;
    try {
      return await task();
    } finally {
      _running--;
      if (_waiting.isNotEmpty) {
        _waiting.removeFirst().complete();
      }
    }
  }
}

/// The width a page shows first (T-405): small enough to come at once; the
/// sharp one follows.
const quickPageWidth = 640;

/// Pages swiped one at a time with "1 / N" under them: a file's zoomed
/// view, or a shared file's before it is saved (docs/DESIGN_SYSTEM.md,
/// "Mobil: fayl paylaşımı"). Under the page, buttons to zoom and to move
/// the zoomed page (T-405); while zoomed, a swipe moves the page, not to
/// the next one.
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
  static const _maxScale = 5.0;
  static const _zoomStep = 1.5;

  /// A quarter of the view a press.
  static const _moveStep = 0.25;

  late int _current = widget.initialPage;
  late final _controller = PageController(initialPage: widget.initialPage - 1);
  final _views = <int, TransformationController>{};
  Size _viewport = Size.zero;
  bool _zoomed = false;

  @override
  void dispose() {
    _controller.dispose();
    for (final view in _views.values) {
      view.dispose();
    }
    super.dispose();
  }

  TransformationController _view(int page) => _views.putIfAbsent(page, () {
    final view = TransformationController();
    view.addListener(() {
      if (page == _current) {
        _onZoom(view);
      }
    });
    return view;
  });

  void _onZoom(TransformationController view) {
    final zoomed = view.value.getMaxScaleOnAxis() > 1.01;
    if (zoomed != _zoomed) {
      setState(() => _zoomed = zoomed);
    }
  }

  /// [scale] and [translation] kept inside the page: never smaller than the
  /// view, never moved off it.
  Matrix4 _clamped(double scale, Offset translation) {
    final s = scale.clamp(1.0, _maxScale);
    final minX = -(s - 1) * _viewport.width;
    final minY = -(s - 1) * _viewport.height;
    final x = translation.dx.clamp(minX, 0.0);
    final y = translation.dy.clamp(minY, 0.0);
    return Matrix4.identity()
      ..translateByDouble(x, y, 0, 1)
      ..scaleByDouble(s, s, 1, 1);
  }

  void _zoom(double factor) {
    final view = _view(_current);
    final matrix = view.value;
    final scale = matrix.getMaxScaleOnAxis();
    final now = Offset(matrix.getTranslation().x, matrix.getTranslation().y);
    final centre = _viewport.center(Offset.zero);
    // The point in the middle of the view stays there.
    final point = (centre - now) / scale;
    final next = (scale * factor).clamp(1.0, _maxScale);
    view.value = _clamped(next, centre - point * next);
  }

  void _move(double dx, double dy) {
    final view = _view(_current);
    final matrix = view.value;
    final scale = matrix.getMaxScaleOnAxis();
    final now = Offset(matrix.getTranslation().x, matrix.getTranslation().y);
    view.value = _clamped(
      scale,
      now -
          Offset(
            dx * _viewport.width * _moveStep,
            dy * _viewport.height * _moveStep,
          ),
    );
  }

  void _fit() => _view(_current).value = Matrix4.identity();

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    return Column(
      children: [
        Expanded(
          child: LayoutBuilder(
            builder: (context, constraints) {
              _viewport = constraints.biggest;
              return PageView.builder(
                controller: _controller,
                // Zoomed, a swipe moves the page (T-405).
                physics: _zoomed ? const NeverScrollableScrollPhysics() : null,
                itemCount: widget.pageCount,
                onPageChanged: (index) {
                  setState(() => _current = index + 1);
                  _onZoom(_view(_current));
                },
                itemBuilder: (context, index) => _PageImage(
                  load: widget.load,
                  page: index + 1,
                  total: widget.pageCount,
                  quarterTurns: widget.quarterTurns,
                  view: _view(index + 1),
                  maxScale: _maxScale,
                ),
              );
            },
          ),
        ),
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: Spacing.x2),
          child: Wrap(
            alignment: WrapAlignment.center,
            children: [
              IconButton(
                tooltip: l10n.previewZoomOut,
                onPressed: _zoomed ? () => _zoom(1 / _zoomStep) : null,
                icon: const Icon(Icons.zoom_out),
              ),
              IconButton(
                tooltip: l10n.previewZoomIn,
                onPressed: () => _zoom(_zoomStep),
                icon: const Icon(Icons.zoom_in),
              ),
              IconButton(
                tooltip: l10n.previewMoveLeft,
                onPressed: _zoomed ? () => _move(-1, 0) : null,
                icon: const Icon(Icons.arrow_back),
              ),
              IconButton(
                tooltip: l10n.previewMoveUp,
                onPressed: _zoomed ? () => _move(0, -1) : null,
                icon: const Icon(Icons.arrow_upward),
              ),
              IconButton(
                tooltip: l10n.previewMoveDown,
                onPressed: _zoomed ? () => _move(0, 1) : null,
                icon: const Icon(Icons.arrow_downward),
              ),
              IconButton(
                tooltip: l10n.previewMoveRight,
                onPressed: _zoomed ? () => _move(1, 0) : null,
                icon: const Icon(Icons.arrow_forward),
              ),
              IconButton(
                tooltip: l10n.previewFit,
                onPressed: _zoomed ? _fit : null,
                icon: const Icon(Icons.fit_screen_outlined),
              ),
            ],
          ),
        ),
        Padding(
          padding: const EdgeInsets.only(bottom: Spacing.x3),
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
    this.view,
    this.maxScale = 5,
  });

  final PageLoader load;
  final int page;
  final int total;
  final int quarterTurns;
  final TransformationController? view;
  final double maxScale;

  @override
  State<_PageImage> createState() => _PageImageState();
}

class _PageImageState extends State<_PageImage> {
  /// What is shown: the quick page, then the sharp one over it.
  Uint8List? _bytes;
  Object? _error;
  int? _width;

  /// Which load is the latest; an older one's answer is dropped.
  int _attempt = 0;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();

    // Physical pixels, so the page is sharp; the server rounds to a few sizes.
    final media = MediaQuery.of(context);
    final width = (media.size.width * media.devicePixelRatio).round();

    if (width != _width) {
      _width = width;
      unawaited(_load());
    }
  }

  /// A small page at once, then the sharp one (T-405): the first look does
  /// not wait for the large render.
  Future<void> _load() async {
    final attempt = ++_attempt;
    final sharp = _width!;
    setState(() => _error = null);

    try {
      if (_bytes == null && sharp > quickPageWidth) {
        final quick = await widget.load(widget.page, quickPageWidth);
        if (!mounted || attempt != _attempt) {
          return;
        }
        setState(() => _bytes = quick);
      }

      final bytes = await widget.load(widget.page, sharp);
      if (mounted && attempt == _attempt) {
        setState(() => _bytes = bytes);
      }
    } on Object catch (error) {
      // The quick page stays when only the sharp one failed.
      if (mounted && attempt == _attempt && _bytes == null) {
        setState(() => _error = error);
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final tokens = context.tokens;
    final error = _error;
    final bytes = _bytes;

    if (error != null) {
      // The server's own sentence when it sent one - "the document service
      // is not running, try again shortly" tells more than "the page could
      // not be shown" - and no connection in the app's words.
      final reason =
          error is ApiException &&
              (error.kind != ApiErrorKind.server || error.message.isNotEmpty)
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
                onPressed: () => unawaited(_load()),
                child: Text(l10n.commonRetry),
              ),
            ],
          ),
        ),
      );
    }

    if (bytes == null) {
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
      transformationController: widget.view,
      maxScale: widget.maxScale,
      child: Center(
        child: RotatedBox(
          quarterTurns: widget.quarterTurns,
          child: Container(
            margin: const EdgeInsets.all(Spacing.x4),
            color: LightPalette.neutral0,
            child: Image.memory(
              bytes,
              fit: BoxFit.contain,
              // The sharp page replaces the quick one without a blink.
              gaplessPlayback: true,
              semanticLabel: l10n.filesPageOf(widget.page, widget.total),
            ),
          ),
        ),
      ),
    );
  }
}
