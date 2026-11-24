import 'dart:async';
import 'dart:math' as math;
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_primary_button.dart';
import '../files/file_models.dart';
import '../files/files_controller.dart';
import 'layout_canvas.dart';
import 'layout_editing.dart';
import 'layout_model.dart';
import 'layout_repository.dart';
import 'layout_templates.dart';

/// Pictures brought into the editor: their bytes and what is known of them,
/// by name.
typedef LoadedPictures = (
  Map<String, Uint8List> images,
  Map<String, EditorPicture> pictures,
);

/// What the editor is working on.
enum LayoutEditorMode {
  /// A scan's arrangement: "Hazır" hands the layout back to the scan screen.
  scan,

  /// A new layout file of pictures and PDF pages.
  newFile,

  /// A layout file opened again: saving makes a new version.
  editFile,
}

/// The page layout editor (plan section 2.11; docs/DESIGN_SYSTEM.md "Mobil:
/// səhifə tərtibatı redaktoru"). In [LayoutEditorMode.scan] it returns the
/// changed layout; otherwise it saves the layout file itself and returns
/// the saved file.
class LayoutEditorScreen extends ConsumerStatefulWidget {
  const LayoutEditorScreen({
    super.key,
    required this.initial,
    required this.pictures,
    required this.images,
    required this.mode,
    this.fileId,
    this.title,
    this.onAddPictures,
  });

  final LayoutProject initial;

  /// What is known about each picture, by name.
  final Map<String, EditorPicture> pictures;

  /// The pictures' bytes, by name.
  final Map<String, Uint8List> images;
  final LayoutEditorMode mode;

  /// The layout file, in [LayoutEditorMode.editFile].
  final String? fileId;
  final String? title;

  /// Chooses more files and loads them (T-409); null where nothing can be
  /// added (a scan's own pages).
  final Future<LoadedPictures?> Function(BuildContext context)? onAddPictures;

  @override
  ConsumerState<LayoutEditorScreen> createState() => _LayoutEditorScreenState();
}

class _LayoutEditorScreenState extends ConsumerState<LayoutEditorScreen> {
  /// Room between two pages in the list (T-453).
  static const _gap = Spacing.x2;

  /// The most of the screen's height one page takes (T-454).
  static const _pageShare = 0.6;

  late LayoutProject _project = widget.initial;

  /// The page worked on: the tools and "Səhifə əlavə et" act on it.
  int _page = 0;
  final _scroll = ScrollController();

  /// One page's height in the list, with the room under it.
  double _extent = 0;
  int? _selected;

  /// "Çoxlu seç" (T-430): a tap adds a picture to [_marked] or takes it out,
  /// and the tools act on all of them.
  bool _multi = false;
  Set<int> _marked = const {};

  /// "Stili köçür" (T-430): the size and turn to give others.
  LayoutElement? _style;
  bool _lockRatio = true;
  bool _dirty = false;
  bool _saving = false;
  String? _error;
  Map<String, ui.Image> _decoded = const {};

  /// The pictures, with the ones added here (T-409).
  late final Map<String, EditorPicture> _pictures = {...widget.pictures};

  @override
  void initState() {
    super.initState();
    unawaited(_decode(widget.images));
  }

  @override
  void dispose() {
    _scroll.dispose();
    for (final image in _decoded.values) {
      image.dispose();
    }
    super.dispose();
  }

  Future<void> _decode(Map<String, Uint8List> images) async {
    final decoded = <String, ui.Image>{..._decoded};

    for (final MapEntry(key: name, value: bytes) in images.entries) {
      if (decoded.containsKey(name)) {
        continue;
      }
      final codec = await ui.instantiateImageCodec(bytes);
      decoded[name] = (await codec.getNextFrame()).image;
      codec.dispose();
    }

    if (mounted) {
      setState(() => _decoded = decoded);
    } else {
      for (final image in decoded.values) {
        image.dispose();
      }
    }
  }

  /// More files onto this page (T-409): one in the middle, several side by
  /// side; the last one comes selected.
  Future<void> _addPictures() async {
    final loaded = await widget.onAddPictures!(context);
    if (loaded == null || !mounted) {
      return;
    }

    final (images, pictures) = loaded;
    _pictures.addAll(pictures);
    final added = [for (final name in images.keys) _pictures[name]!];
    if (added.isEmpty) {
      return;
    }

    // Many at once go on new pages after this one (T-430).
    final project = added.length == 1
        ? LayoutEditing.add(_project, _page, added.single)
        : LayoutEditing.arrangePaged(_project, _page, added);
    _apply(project, select: project.pages[_page].elements.length - 1);
    await _decode(images);
  }

  List<LayoutElement> get _elements => _project.pages[_page].elements;

  /// Pictures on every page together.
  int get _pictureCount =>
      _project.pages.fold(0, (sum, page) => sum + page.elements.length);

  void _goTo(int page) => setState(() {
    _page = page;
    _selected = null;
    _marked = const {};
  });

  /// An empty page after this one, shown at once (T-451).
  void _addPage() {
    _apply(LayoutEditing.addPage(_project, _page));
    _show(_page + 1);
  }

  /// This page and its pictures; asked first when it has any (T-451).
  Future<void> _deletePage() async {
    if (_elements.isNotEmpty) {
      final l10n = AppLocalizations.of(context);
      final sure = await showDialog<bool>(
        context: context,
        builder: (context) => AlertDialog(
          title: Text(l10n.layoutDeletePageTitle),
          content: Text(l10n.layoutDeletePageBody),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(context, false),
              child: Text(l10n.commonCancel),
            ),
            FilledButton(
              onPressed: () => Navigator.pop(context, true),
              child: Text(l10n.layoutDelete),
            ),
          ],
        ),
      );
      if (sure != true || !mounted) {
        return;
      }
    }

    final page = _page;
    _apply(LayoutEditing.deletePage(_project, page));
    _show(page == 0 ? 0 : page - 1);
  }

  /// [page] becomes the one worked on and is scrolled into sight (T-453).
  void _show(int page) {
    setState(() => _page = page);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || !_scroll.hasClients || _extent == 0) {
        return;
      }
      final position = _scroll.position;
      final top = page * _extent;
      final bottom = top + _extent - _gap;
      if (top >= position.pixels &&
          bottom <= position.pixels + position.viewportDimension) {
        return;
      }
      unawaited(
        _scroll.animateTo(
          top.clamp(0, position.maxScrollExtent),
          duration: Motion.base,
          curve: Curves.easeOut,
        ),
      );
    });
  }

  /// How much of [page] the list shows, from 0 to 1.
  double _seen(int page, ScrollMetrics metrics) {
    final height = _extent - _gap;
    final top = page * _extent;
    final shown =
        math.min(top + height, metrics.pixels + metrics.viewportDimension) -
        math.max(top, metrics.pixels);

    return (shown / height).clamp(0, 1);
  }

  /// Scrolled to another page: once the page worked on is mostly out of
  /// sight, the one most in sight takes its place (T-453).
  bool _settled(ScrollEndNotification note) {
    if (note.depth != 0 || _extent == 0) {
      return false;
    }
    final metrics = note.metrics;
    if (_seen(_page, metrics) >= 0.5) {
      return false;
    }

    var best = _page;
    for (var page = 0; page < _project.pages.length; page++) {
      if (_seen(page, metrics) > _seen(best, metrics)) {
        best = page;
      }
    }
    if (best != _page) {
      _goTo(best);
    }
    return false;
  }

  /// A finger came down on [page]: that page is worked on from now, and
  /// [index] is the picture under it, if any.
  void _select(int page, int? index) => setState(() {
    if (page != _page) {
      _page = page;
      _selected = null;
      _marked = const {};
    }
    if (!_multi) {
      _selected = index;
      return;
    }
    // Several at once: a tap adds or takes out (T-430).
    _selected = null;
    if (index != null) {
      _marked = _marked.contains(index)
          ? ({..._marked}..remove(index))
          : {..._marked, index};
    }
  });

  /// The pages one under another (T-453): each as wide as the screen, not
  /// taller than the list, so the next one shows below it.
  Widget _pageList(NeoPrintTokens tokens) => LayoutBuilder(
    builder: (context, constraints) {
      final (width, height) = _project.pageSizeMm;
      const chrome = LayoutCanvas.chrome;
      final fit = (constraints.maxWidth - chrome) * height / width + chrome;
      // Never from the list's own height (T-454): a banner, the size line
      // or a toolbar coming and going would rescale the page - the rulers
      // would jump and the picture under the finger would run off. The
      // screen's height changes only when the phone is turned.
      final room = MediaQuery.sizeOf(context).height * _pageShare;
      final pageHeight = math.max(math.min(fit, room), chrome);
      _extent = pageHeight + _gap;

      return ColoredBox(
        color: tokens.neutral100,
        child: NotificationListener<ScrollEndNotification>(
          onNotification: _settled,
          child: ListView.builder(
            controller: _scroll,
            itemExtent: _extent,
            itemCount: _project.pages.length,
            itemBuilder: (context, page) => Padding(
              padding: const EdgeInsets.only(bottom: _gap),
              child: LayoutCanvas(
                project: _project,
                page: page,
                images: _decoded,
                selected: page == _page ? _selected : null,
                lockRatio: _lockRatio,
                marked: _multi && page == _page ? _marked : const {},
                highlighted: page == _page && _project.pages.length > 1,
                onSelect: (index) => _select(page, index),
                onChanged: (index, element, {required done}) {
                  setState(() {
                    _project = LayoutEditing.replace(
                      _project,
                      page,
                      index,
                      element,
                    );
                    _dirty = true;
                  });
                  if (done) {
                    _carryOver(index);
                  }
                },
              ),
            ),
          ),
        ),
      );
    },
  );

  /// A picture let go past the bottom of the page goes onto the next one -
  /// made if there is none - and past the top, onto the one before (T-451).
  void _carryOver(int index) {
    if (_project.pages.length >= LayoutEditing.maxPages &&
        _page == _project.pages.length - 1) {
      return;
    }
    final carried = LayoutEditing.carryOver(_project, _page, index);
    if (carried == null) {
      return;
    }

    final (project, page, select) = carried;
    _apply(project, select: select);
    _show(page);

    final l10n = AppLocalizations.of(context);
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(
        SnackBar(
          content: Text(l10n.layoutCarried(page + 1, project.pages.length)),
        ),
      );
  }

  LayoutElement? get _current =>
      _selected != null && _selected! < _elements.length
      ? _elements[_selected!]
      : null;

  void _apply(LayoutProject project, {int? select}) => setState(() {
    _project = project;
    _selected = select;
    _marked = const {};
    _dirty = true;
    _error = null;
  });

  void _replace(LayoutElement element) => _apply(
    LayoutEditing.replace(_project, _page, _selected!, element),
    select: _selected,
  );

  /// A change to the pictures chosen together; they stay chosen.
  void _keepMarked(LayoutProject project) {
    final marked = _marked;
    _apply(project);
    setState(() => _marked = marked);
  }

  /// A row of tools under the canvas, scrolling sideways when long.
  Widget _toolbar(List<Widget> tools) => SingleChildScrollView(
    scrollDirection: Axis.horizontal,
    padding: const EdgeInsets.symmetric(horizontal: Spacing.x2),
    child: Row(children: tools),
  );

  void _template(LayoutTemplate template) {
    final pictures = [
      for (final name in _project.images)
        () {
          final picture = _pictures[name];
          final used = _project.pages
              .expand((page) => page.elements)
              .firstWhere((element) => element.image == name);
          return picture != null && picture.hasRealSize
              ? TemplatePicture(
                  name,
                  picture.realWidthMm!,
                  picture.realHeightMm!,
                )
              : TemplatePicture(name, used.widthMm, used.heightMm);
        }(),
    ];

    if (pictures.isNotEmpty) {
      _apply(LayoutTemplates.apply(template, pictures, _project.marginMm));
      _show(0);
    }
  }

  Future<void> _paperSheet() async {
    final l10n = AppLocalizations.of(context);

    await showModalBottomSheet<void>(
      context: context,
      showDragHandle: true,
      builder: (context) => StatefulBuilder(
        builder: (context, setSheet) => SafeArea(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(
              Spacing.x4,
              0,
              Spacing.x4,
              Spacing.x4,
            ),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(
                  l10n.layoutPaper,
                  style: Theme.of(context).textTheme.titleMedium,
                ),
                const SizedBox(height: Spacing.x3),
                SegmentedButton<LayoutPaper>(
                  segments: [
                    for (final paper in LayoutPaper.values)
                      ButtonSegment(value: paper, label: Text(paper.wire)),
                  ],
                  selected: {_project.paper},
                  onSelectionChanged: (value) {
                    _apply(
                      _project.copyWith(paper: value.single),
                      select: _selected,
                    );
                    setSheet(() {});
                  },
                ),
                const SizedBox(height: Spacing.x3),
                SegmentedButton<LayoutOrientation>(
                  segments: [
                    ButtonSegment(
                      value: LayoutOrientation.portrait,
                      label: Text(l10n.layoutPortrait),
                    ),
                    ButtonSegment(
                      value: LayoutOrientation.landscape,
                      label: Text(l10n.layoutLandscape),
                    ),
                  ],
                  selected: {_project.orientation},
                  onSelectionChanged: (value) {
                    _apply(
                      _project.copyWith(orientation: value.single),
                      select: _selected,
                    );
                    setSheet(() {});
                  },
                ),
                const SizedBox(height: Spacing.x3),
                Text(l10n.layoutMargin(_project.marginMm.round())),
                Slider(
                  max: 30,
                  divisions: 30,
                  value: _project.marginMm.clamp(0, 30),
                  onChanged: (value) {
                    _apply(
                      _project.copyWith(marginMm: value.roundToDouble()),
                      select: _selected,
                    );
                    setSheet(() {});
                  },
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  Future<void> _done() async {
    // A page left empty is not saved (T-451).
    final project = LayoutEditing.withoutEmptyPages(_project);

    if (widget.mode == LayoutEditorMode.scan) {
      Navigator.pop(context, project);
      return;
    }

    final l10n = AppLocalizations.of(context);
    String? name;

    if (widget.mode == LayoutEditorMode.newFile) {
      name = await _askName(l10n);
      if (name == null || !mounted) {
        return;
      }
    }

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      final repository = ref.read(layoutRepositoryProvider);
      final UserFile saved = widget.mode == LayoutEditorMode.editFile
          ? await repository.update(widget.fileId!, project)
          : await repository.create(project, name!);

      unawaited(ref.read(filesControllerProvider.notifier).load());

      if (mounted) {
        _dirty = false;
        Navigator.pop(context, saved);
      }
    } on ApiException catch (error) {
      if (mounted) {
        setState(() {
          _saving = false;
          _error = l10n.layoutSaveFailed(apiErrorText(l10n, error));
        });
      }
    }
  }

  Future<String?> _askName(AppLocalizations l10n) {
    final controller = TextEditingController(
      text: widget.title ?? l10n.layoutTitle,
    );

    return showDialog<String>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(l10n.layoutNameTitle),
        content: TextField(controller: controller, autofocus: true),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: Text(l10n.commonCancel),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(
              context,
              controller.text.trim().isEmpty
                  ? l10n.layoutTitle
                  : controller.text.trim(),
            ),
            child: Text(l10n.layoutSave),
          ),
        ],
      ),
    ).whenComplete(controller.dispose);
  }

  Future<bool> _confirmLeave() async {
    final l10n = AppLocalizations.of(context);

    return await showDialog<bool>(
          context: context,
          builder: (context) => AlertDialog(
            title: Text(l10n.layoutUnsavedTitle),
            content: Text(l10n.layoutUnsavedBody),
            actions: [
              TextButton(
                onPressed: () => Navigator.pop(context, false),
                child: Text(l10n.layoutUnsavedStay),
              ),
              FilledButton(
                onPressed: () => Navigator.pop(context, true),
                child: Text(l10n.layoutUnsavedLeave),
              ),
            ],
          ),
        ) ??
        false;
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final tokens = context.tokens;
    final language = Localizations.localeOf(context).languageCode;
    final current = _current;
    final offPage = LayoutEditing.hasOffPage(_project);
    final edge = !offPage && LayoutEditing.leavesPrintableArea(_project);
    final picture = current == null ? null : _pictures[current.image];

    return PopScope(
      canPop: !_dirty || _saving,
      onPopInvokedWithResult: (didPop, _) async {
        if (!didPop && await _confirmLeave() && context.mounted) {
          _dirty = false;
          Navigator.pop(context);
        }
      },
      child: Scaffold(
        appBar: AppBar(
          title: Text(widget.title ?? l10n.layoutTitle),
          actions: [
            if (widget.onAddPictures != null)
              IconButton(
                tooltip: l10n.layoutAddFile,
                icon: const Icon(Icons.add_photo_alternate_outlined),
                onPressed: _saving ? null : () => unawaited(_addPictures()),
              ),
            PopupMenuButton<LayoutTemplate>(
              tooltip: l10n.layoutTemplate,
              icon: const Icon(Icons.grid_view),
              onSelected: _template,
              itemBuilder: (_) => [
                for (final (template, label) in [
                  (LayoutTemplate.idFrontBack, l10n.docScanArrangeIdFrontBack),
                  (LayoutTemplate.grid2x2, l10n.docScanArrangeGrid2x2),
                  (LayoutTemplate.cards2x4, l10n.docScanArrangeCards2x4),
                  (LayoutTemplate.photo3x4x6, l10n.docScanArrangePhoto3x4x6),
                  (
                    LayoutTemplate.passportCentered,
                    l10n.docScanArrangePassport,
                  ),
                ])
                  PopupMenuItem(value: template, child: Text(label)),
              ],
            ),
            IconButton(
              tooltip: l10n.layoutPaper,
              icon: const Icon(Icons.description_outlined),
              onPressed: () => unawaited(_paperSheet()),
            ),
          ],
        ),
        body: SafeArea(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              // Pages one under another, scrolled (T-453).
              Expanded(child: _pageList(tokens)),
              // Under the pages, not over them (T-454): coming while a
              // picture is dragged, they must not push the pages down.
              if (offPage)
                Padding(
                  padding: const EdgeInsets.all(Spacing.x2),
                  child: NeoBanner.error(message: l10n.layoutOffPage),
                )
              else if (edge)
                Padding(
                  padding: const EdgeInsets.all(Spacing.x2),
                  child: NeoBanner(
                    tone: StatusTone.warning,
                    message: l10n.layoutEdgeWarning,
                  ),
                ),
              // Pages (T-451): always here, so a second one can be made.
              Row(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  Flexible(
                    child: Text(
                      l10n.layoutPageOf(_page + 1, _project.pages.length),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  IconButton(
                    tooltip: l10n.layoutAddPage,
                    visualDensity: VisualDensity.compact,
                    onPressed: _project.pages.length < LayoutEditing.maxPages
                        ? _addPage
                        : null,
                    icon: const Icon(Icons.post_add),
                  ),
                  if (_project.pages.length > 1)
                    IconButton(
                      tooltip: l10n.layoutDeletePage,
                      visualDensity: VisualDensity.compact,
                      onPressed: () => unawaited(_deletePage()),
                      icon: Icon(
                        Icons.delete_sweep_outlined,
                        color: tokens.danger,
                      ),
                    ),
                ],
              ),
              if (_elements.isEmpty)
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: Spacing.x4),
                  child: Text(
                    widget.onAddPictures != null
                        ? l10n.layoutPageEmpty
                        : l10n.layoutPageEmptyScan,
                    textAlign: TextAlign.center,
                    style: Theme.of(context).textTheme.bodySmall
                        ?.copyWith(color: tokens.neutral600),
                  ),
                ),
              // Several pictures at once (T-430).
              if (_multi)
                _toolbar([
                  _Tool(
                    icon: Icons.select_all,
                    label: l10n.layoutSelectAll,
                    onPressed: () => setState(
                      () => _marked = {
                        for (var i = 0; i < _elements.length; i++) i,
                      },
                    ),
                  ),
                  if (_marked.isNotEmpty) ...[
                    _Tool(
                      icon: Icons.rotate_right,
                      label: l10n.layoutRotate90,
                      onPressed: () => _keepMarked(
                        LayoutEditing.rotateMany(_project, _page, _marked),
                      ),
                    ),
                    if (_style case final style?)
                      _Tool(
                        icon: Icons.format_paint,
                        label: l10n.layoutPasteStyle,
                        onPressed: () => _keepMarked(
                          LayoutEditing.applyStyle(
                            _project,
                            _page,
                            _marked,
                            style,
                            _pictures,
                          ),
                        ),
                      ),
                    _Tool(
                      icon: Icons.grid_view,
                      label: l10n.layoutArrangeEqual,
                      onPressed: () => _apply(
                        LayoutEditing.arrangeElements(
                          _project,
                          _page,
                          _marked,
                          _pictures,
                        ),
                      ),
                    ),
                    _Tool(
                      icon: Icons.note_add_outlined,
                      label: l10n.layoutToNewPage,
                      onPressed: () {
                        _apply(
                          LayoutEditing.moveToNewPage(
                            _project,
                            _page,
                            _marked,
                            _pictures,
                          ),
                        );
                        _show(_page + 1);
                      },
                    ),
                    if (_marked.length < _pictureCount)
                      _Tool(
                        icon: Icons.delete_outline,
                        label: l10n.layoutDelete,
                        danger: true,
                        onPressed: () => _apply(
                          LayoutEditing.deleteMany(_project, _page, _marked),
                        ),
                      ),
                  ],
                  _Tool(
                    icon: Icons.close,
                    label: l10n.layoutMultiDone,
                    onPressed: () => setState(() {
                      _multi = false;
                      _marked = const {};
                    }),
                  ),
                ])
              // Nothing chosen: what acts on the whole page (T-430).
              else if (current == null && _elements.length > 1)
                _toolbar([
                  _Tool(
                    icon: Icons.checklist,
                    label: l10n.layoutMultiSelect,
                    onPressed: () => setState(() => _multi = true),
                  ),
                  _Tool(
                    icon: Icons.grid_view,
                    label: l10n.layoutArrangeEqual,
                    onPressed: () => _apply(
                      LayoutEditing.arrangeElements(_project, _page, [
                        for (var i = 0; i < _elements.length; i++) i,
                      ], _pictures),
                    ),
                  ),
                ]),
              if (current != null && !_multi) ...[
                Padding(
                  padding: const EdgeInsets.only(top: Spacing.x1),
                  child: Text(
                    '${formatNumber(current.widthMm, language: language)} × '
                    '${formatNumber(current.heightMm, language: language)} mm',
                    textAlign: TextAlign.center,
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: tokens.neutral600,
                      fontFeatures: const [ui.FontFeature.tabularFigures()],
                    ),
                  ),
                ),
                SingleChildScrollView(
                  scrollDirection: Axis.horizontal,
                  padding: const EdgeInsets.symmetric(horizontal: Spacing.x2),
                  child: Row(
                    children: [
                      _Tool(
                        icon: Icons.rotate_right,
                        label: l10n.layoutRotate90,
                        onPressed: () =>
                            _replace(LayoutEditing.rotate90(current)),
                      ),
                      if (picture != null && picture.hasRealSize)
                        _Tool(
                          icon: Icons.straighten,
                          label: l10n.layoutRealSize,
                          onPressed: () => _replace(
                            LayoutEditing.realSize(current, picture),
                          ),
                        ),
                      _Tool(
                        icon: _lockRatio ? Icons.link : Icons.link_off,
                        label: l10n.layoutRatio,
                        selected: _lockRatio,
                        onPressed: () =>
                            setState(() => _lockRatio = !_lockRatio),
                      ),
                      _Tool(
                        icon: Icons.content_copy,
                        label: l10n.layoutDuplicate,
                        onPressed: () => _apply(
                          LayoutEditing.duplicate(_project, _page, _selected!),
                          select: _elements.length,
                        ),
                      ),
                      _Tool(
                        icon: Icons.flip_to_front,
                        label: l10n.layoutToFront,
                        onPressed: () => _apply(
                          LayoutEditing.toFront(_project, _page, _selected!),
                          select: _elements.length - 1,
                        ),
                      ),
                      _Tool(
                        icon: Icons.flip_to_back,
                        label: l10n.layoutToBack,
                        onPressed: () => _apply(
                          LayoutEditing.toBack(_project, _page, _selected!),
                          select: 0,
                        ),
                      ),
                      _Tool(
                        icon: Icons.center_focus_strong,
                        label: l10n.layoutCentre,
                        onPressed: () =>
                            _replace(LayoutEditing.centre(_project, current)),
                      ),
                      // Its size and turn, to give the others (T-430).
                      _Tool(
                        icon: Icons.style_outlined,
                        label: l10n.layoutCopyStyle,
                        selected: _style == current,
                        onPressed: () {
                          setState(() => _style = current);
                          ScaffoldMessenger.of(context).showSnackBar(
                            SnackBar(content: Text(l10n.layoutStyleCopied)),
                          );
                        },
                      ),
                      if (_style case final style? when style != current)
                        _Tool(
                          icon: Icons.format_paint,
                          label: l10n.layoutPasteStyle,
                          onPressed: () => _apply(
                            LayoutEditing.applyStyle(
                              _project,
                              _page,
                              [_selected!],
                              style,
                              _pictures,
                            ),
                            select: _selected,
                          ),
                        ),
                      if (_elements.length > 1)
                        _Tool(
                          icon: Icons.checklist,
                          label: l10n.layoutMultiSelect,
                          onPressed: () => setState(() {
                            _multi = true;
                            _marked = {_selected!};
                            _selected = null;
                          }),
                        ),
                      if (_pictureCount > 1)
                        _Tool(
                          icon: Icons.delete_outline,
                          label: l10n.layoutDelete,
                          danger: true,
                          onPressed: () => _apply(
                            LayoutEditing.delete(_project, _page, _selected!),
                          ),
                        ),
                    ],
                  ),
                ),
              ],
              if (_error case final error?)
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: Spacing.x4),
                  child: NeoBanner.error(message: error),
                ),
              Padding(
                padding: const EdgeInsets.all(Spacing.x4),
                child: NeoPrimaryButton(
                  label: widget.mode == LayoutEditorMode.scan
                      ? l10n.layoutDone
                      : _saving
                      ? l10n.layoutSaving
                      : l10n.layoutSave,
                  busy: _saving,
                  onPressed: offPage || _saving || _pictureCount == 0
                      ? null
                      : () => unawaited(_done()),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// One tool under the canvas: an icon over its name.
class _Tool extends StatelessWidget {
  const _Tool({
    required this.icon,
    required this.label,
    required this.onPressed,
    this.selected = false,
    this.danger = false,
  });

  final IconData icon;
  final String label;
  final VoidCallback onPressed;
  final bool selected;
  final bool danger;

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;
    final colour = danger
        ? tokens.danger
        : selected
        ? tokens.brand500
        : tokens.neutral800;

    return InkWell(
      onTap: onPressed,
      borderRadius: BorderRadius.circular(Radii.sm),
      child: Padding(
        padding: const EdgeInsets.symmetric(
          horizontal: Spacing.x3,
          vertical: Spacing.x2,
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, color: colour),
            const SizedBox(height: Spacing.x1),
            Text(
              label,
              style: Theme.of(context).textTheme.bodySmall
                  ?.copyWith(color: colour),
            ),
          ],
        ),
      ),
    );
  }
}
