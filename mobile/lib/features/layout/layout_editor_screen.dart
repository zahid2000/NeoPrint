import 'dart:async';
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
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

  @override
  ConsumerState<LayoutEditorScreen> createState() => _LayoutEditorScreenState();
}

class _LayoutEditorScreenState extends ConsumerState<LayoutEditorScreen> {
  late LayoutProject _project = widget.initial;
  int _page = 0;
  int? _selected;
  bool _lockRatio = true;
  bool _dirty = false;
  bool _saving = false;
  String? _error;
  Map<String, ui.Image> _decoded = const {};

  @override
  void initState() {
    super.initState();
    unawaited(_decode());
  }

  @override
  void dispose() {
    for (final image in _decoded.values) {
      image.dispose();
    }
    super.dispose();
  }

  Future<void> _decode() async {
    final decoded = <String, ui.Image>{};

    for (final MapEntry(key: name, value: bytes) in widget.images.entries) {
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

  List<LayoutElement> get _elements => _project.pages[_page].elements;

  LayoutElement? get _current =>
      _selected != null && _selected! < _elements.length
      ? _elements[_selected!]
      : null;

  void _apply(LayoutProject project, {int? select}) => setState(() {
    _project = project;
    _selected = select;
    _dirty = true;
    _error = null;
  });

  void _replace(LayoutElement element) => _apply(
    LayoutEditing.replace(_project, _page, _selected!, element),
    select: _selected,
  );

  void _template(LayoutTemplate template) {
    final pictures = [
      for (final name in _project.images)
        () {
          final picture = widget.pictures[name];
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
      _page = 0;
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
    if (widget.mode == LayoutEditorMode.scan) {
      Navigator.pop(context, _project);
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
          ? await repository.update(widget.fileId!, _project)
          : await repository.create(_project, name!);

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
    final picture = current == null ? null : widget.pictures[current.image];

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
              Expanded(
                child: LayoutCanvas(
                  project: _project,
                  page: _page,
                  images: _decoded,
                  selected: _selected,
                  lockRatio: _lockRatio,
                  onSelect: (index) => setState(() => _selected = index),
                  onChanged: (index, element, {required done}) => setState(() {
                    _project = LayoutEditing.replace(
                      _project,
                      _page,
                      index,
                      element,
                    );
                    _dirty = true;
                  }),
                ),
              ),
              if (_project.pages.length > 1)
                Row(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    IconButton(
                      onPressed: _page > 0
                          ? () => setState(() {
                              _page--;
                              _selected = null;
                            })
                          : null,
                      icon: const Icon(Icons.chevron_left),
                    ),
                    Text(l10n.layoutPageOf(_page + 1, _project.pages.length)),
                    IconButton(
                      onPressed: _page < _project.pages.length - 1
                          ? () => setState(() {
                              _page++;
                              _selected = null;
                            })
                          : null,
                      icon: const Icon(Icons.chevron_right),
                    ),
                  ],
                ),
              if (current != null) ...[
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
                      if (_elements.length > 1)
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
                  onPressed: offPage || _saving
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
