import 'dart:async';
import 'dart:io';
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
import '../files/files_controller.dart';
import '../layout/layout_preview.dart';
import '../layout/layout_templates.dart';
import 'document_scanner.dart';
import 'scan_models.dart';
import 'scan_notice_sheet.dart';
import 'scan_page_editor.dart';
import 'scan_processing.dart';
import 'scanner_repository.dart';

/// How the scanner screen ended, for "Fayllarım".
enum ScanOutcome {
  saved,

  /// No scanner on this phone: take a photo instead.
  useCamera,
}

/// "Skanı hazırla" (plan sections 2.11, 9.7; docs/DESIGN_SYSTEM.md "Mobil:
/// sənəd skanı"): the scanner's pages, their document type, how they lie on
/// paper, and the save that turns them into a finished PDF
/// (docs/DECISIONS.md 2026-10-05: never edited afterwards).
class DocumentScanScreen extends ConsumerStatefulWidget {
  const DocumentScanScreen({super.key, this.scanner = const DocumentScanner()});

  final DocumentScanner scanner;

  @override
  ConsumerState<DocumentScanScreen> createState() => _DocumentScanScreenState();
}

class _DocumentScanScreenState extends ConsumerState<DocumentScanScreen> {
  /// Long side of the previews: enough for a phone screen.
  static const _previewSide = 600;

  final List<ScanPage> _pages = [];
  final Map<String, Uint8List> _previews = {};

  ScanType _type = const ScanType.preset(DocumentPreset.idCard);
  bool _sensitiveChosen = false;
  ScanArrangement _arrangement = ScanArrangement.onePerPage;
  bool _arrangementChosen = false;
  final _customWidth = TextEditingController();
  final _customHeight = TextEditingController();
  final _name = TextEditingController();
  bool _nameEdited = false;

  bool _starting = true;
  bool _saving = false;
  String? _error;
  bool _unavailable = false;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => unawaited(_start()));
  }

  @override
  void dispose() {
    _customWidth.dispose();
    _customHeight.dispose();
    _name.dispose();
    super.dispose();
  }

  Future<void> _start() async {
    if (!await ensureScanNotice(context, ref)) {
      if (mounted) {
        Navigator.pop(context);
      }
      return;
    }

    await _scanMore();

    if (!mounted) {
      return;
    }

    if (_pages.isEmpty && !_unavailable) {
      // Closed the scanner without a page: nothing to prepare.
      _discard();
      Navigator.pop(context);
      return;
    }

    setState(() {
      _starting = false;
      _nameFromType();
    });
  }

  /// The name follows the document type until the user writes one.
  void _nameFromType() {
    if (!_nameEdited) {
      _name.text = _typeLabel(AppLocalizations.of(context), _type);
    }
  }

  Future<void> _scanMore() async {
    final l10n = AppLocalizations.of(context);

    try {
      final paths = await widget.scanner.scan(
        pageLimit: DocumentScanner.maxPages - _pages.length,
      );

      for (final path in paths) {
        final bytes = await File(path).readAsBytes();
        final (width, height) = await _size(bytes);
        _pages.add(ScanPage(path: path, width: width, height: height));
        unawaited(_refreshPreview(_pages.last));
      }

      if (mounted) {
        setState(() {
          _error = null;
          if (!_arrangementChosen) {
            _arrangement = ScanArrangement.forType(_type, _pages.length).first;
          }
        });
      }
    } on ScannerException catch (error) {
      if (mounted) {
        setState(() {
          _unavailable = error.reason == ScannerFailure.unavailable;
          _error = _unavailable ? null : l10n.docScanFailed;
          _starting = false;
        });
      }
    }
  }

  static Future<(int, int)> _size(Uint8List bytes) async {
    final buffer = await ui.ImmutableBuffer.fromUint8List(bytes);
    final descriptor = await ui.ImageDescriptor.encoded(buffer);
    final size = (descriptor.width, descriptor.height);
    descriptor.dispose();
    buffer.dispose();
    return size;
  }

  Future<void> _refreshPreview(ScanPage page) async {
    final processed = await ScanProcessing.processInBackground(
      await File(page.path).readAsBytes(),
      page.edit,
      maxSide: _previewSide,
    );

    if (mounted && _pages.any((candidate) => candidate.path == page.path)) {
      setState(() => _previews[page.path] = processed.jpeg);
    }
  }

  Future<void> _editPage(int index) async {
    final edit = await editScanPage(context, _pages[index], index + 1);

    if (edit != null && mounted) {
      setState(() => _pages[index] = _pages[index].copyWith(edit: edit));
      unawaited(_refreshPreview(_pages[index]));
    }
  }

  void _deletePage(int index) {
    final page = _pages.removeAt(index);
    _previews.remove(page.path);
    unawaited(_deleteQuietly(page.path));

    setState(() {
      if (!_arrangementChosen ||
          !ScanArrangement.forType(
            _type,
            _pages.length,
          ).contains(_arrangement)) {
        _arrangement = ScanArrangement.forType(_type, _pages.length).first;
      }
    });
  }

  void _chooseType(ScanType type) => setState(() {
    _type = type;
    _nameFromType();
    final choices = ScanArrangement.forType(type, _pages.length);
    if (!choices.contains(_arrangement) || !_arrangementChosen) {
      _arrangement = choices.first;
      _arrangementChosen = false;
    }
  });

  ScanType get _effectiveType => _type.isCustom
      ? ScanType.custom(
          parseMillimetres(_customWidth.text),
          parseMillimetres(_customHeight.text),
        )
      : _type;

  bool get _sensitive => _effectiveType.alwaysSensitive || _sensitiveChosen;

  String _typeLabel(AppLocalizations l10n, ScanType type) =>
      switch (type.preset) {
        DocumentPreset.idCard => l10n.docScanTypeIdCard,
        DocumentPreset.id2 => l10n.docScanTypeId2,
        DocumentPreset.passport => l10n.docScanTypePassport,
        DocumentPreset.photo3x4 => l10n.docScanTypePhoto3x4,
        DocumentPreset.photo10x15 => l10n.docScanTypePhoto10x15,
        DocumentPreset.a6 => 'A6',
        DocumentPreset.a5 => 'A5',
        DocumentPreset.a4 => 'A4',
        null => l10n.docScanTypeCustom,
      };

  String _arrangementLabel(AppLocalizations l10n, ScanArrangement value) =>
      switch (value) {
        ScanArrangement.onePerPage => l10n.docScanArrangeOnePerPage,
        ScanArrangement.idFrontBack => l10n.docScanArrangeIdFrontBack,
        ScanArrangement.grid2x2 => l10n.docScanArrangeGrid2x2,
        ScanArrangement.cards2x4 => l10n.docScanArrangeCards2x4,
        ScanArrangement.photo3x4x6 => l10n.docScanArrangePhoto3x4x6,
        ScanArrangement.passportCentered => l10n.docScanArrangePassport,
      };

  Future<void> _save() async {
    final l10n = AppLocalizations.of(context);
    final type = _effectiveType;
    final name = _name.text.trim().isEmpty
        ? _typeLabel(l10n, type)
        : _name.text.trim();

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      final images = <String, Uint8List>{};

      for (final (index, page) in _pages.indexed) {
        final processed = await ScanProcessing.processInBackground(
          await File(page.path).readAsBytes(),
          page.edit,
          widthMm: type.widthMm,
          heightMm: type.heightMm,
        );
        images[scanPageName(index)] = processed.jpeg;
      }

      await ref
          .read(scannerRepositoryProvider)
          .save(
            pages: images,
            layout: scanLayout(_pages, type, _arrangement),
            documentType: type.wire,
            widthMm: type.isCustom ? type.widthMm : null,
            heightMm: type.isCustom ? type.heightMm : null,
            sensitive: _sensitive,
            fileName: name,
          );

      _discard();
      unawaited(ref.read(filesControllerProvider.notifier).load());

      if (mounted) {
        Navigator.pop(context, ScanOutcome.saved);
      }
    } on ApiException catch (error) {
      if (mounted) {
        setState(() {
          _saving = false;
          _error = l10n.docScanSaveFailed(apiErrorText(l10n, error));
        });
      }
    } on FormatException {
      if (mounted) {
        setState(() {
          _saving = false;
          _error = l10n.docScanPageUnreadable;
        });
      }
    }
  }

  /// The scanner's pages are personal documents: they leave the phone's cache
  /// as soon as they are no longer needed.
  void _discard() {
    for (final page in _pages) {
      unawaited(_deleteQuietly(page.path));
    }
  }

  static Future<void> _deleteQuietly(String path) async {
    try {
      await File(path).delete();
    } on FileSystemException {
      // Already gone, or the system cleared the cache.
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    return PopScope(
      onPopInvokedWithResult: (didPop, result) {
        if (didPop && result != ScanOutcome.saved) {
          _discard();
        }
      },
      child: Scaffold(
        appBar: AppBar(title: Text(l10n.docScanTitle)),
        body: _starting
            ? const Center(child: CircularProgressIndicator())
            : _unavailable
            ? _Unavailable(
                onCamera: () => Navigator.pop(context, ScanOutcome.useCamera),
              )
            : _form(context),
      ),
    );
  }

  Widget _form(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final type = _effectiveType;
    final language = Localizations.localeOf(context).languageCode;
    final canSave =
        !_saving &&
        _pages.isNotEmpty &&
        type.isValid &&
        _previews.length == _pages.length;
    final layout = _pages.isEmpty || !type.isValid
        ? null
        : scanLayout(_pages, type, _arrangement);
    final previews = {
      for (final (index, page) in _pages.indexed)
        if (_previews[page.path] case final bytes?) scanPageName(index): bytes,
    };

    return AbsorbPointer(
      absorbing: _saving,
      child: ListView(
        padding: const EdgeInsets.fromLTRB(
          Spacing.x4,
          Spacing.x2,
          Spacing.x4,
          Spacing.x8,
        ),
        children: [
          Text(l10n.docScanPages, style: theme.textTheme.titleSmall),
          const SizedBox(height: Spacing.x2),
          SizedBox(
            height: 148,
            child: ListView.separated(
              scrollDirection: Axis.horizontal,
              itemCount: _pages.length + 1,
              separatorBuilder: (_, __) => const SizedBox(width: Spacing.x3),
              itemBuilder: (context, index) => index == _pages.length
                  ? _AddPageCard(
                      label: l10n.docScanAddPage,
                      onTap: _pages.length < DocumentScanner.maxPages
                          ? () => unawaited(_scanMore())
                          : null,
                    )
                  : _PageThumb(
                      label: l10n.docScanPageN(index + 1),
                      preview: _previews[_pages[index].path],
                      deleteLabel: l10n.docScanDeletePage,
                      onTap: () => unawaited(_editPage(index)),
                      onDelete: _pages.length > 1
                          ? () => _deletePage(index)
                          : null,
                    ),
            ),
          ),
          const SizedBox(height: Spacing.x6),

          Text(l10n.docScanType, style: theme.textTheme.titleSmall),
          const SizedBox(height: Spacing.x2),
          Wrap(
            spacing: Spacing.x2,
            runSpacing: Spacing.x2,
            children: [
              for (final preset in DocumentPreset.values)
                ChoiceChip(
                  label: Text(
                    '${_typeLabel(l10n, ScanType.preset(preset))}\n'
                    '${l10n.docScanSizeMm(formatNumber(preset.widthMm, language: language), formatNumber(preset.heightMm, language: language))}',
                  ),
                  selected: _type.preset == preset,
                  onSelected: (_) => _chooseType(ScanType.preset(preset)),
                ),
              ChoiceChip(
                label: Text(l10n.docScanTypeCustom),
                selected: _type.isCustom,
                onSelected: (_) =>
                    _chooseType(const ScanType.custom(null, null)),
              ),
            ],
          ),
          if (_type.isCustom) ...[
            const SizedBox(height: Spacing.x3),
            Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _customWidth,
                    keyboardType: const TextInputType.numberWithOptions(
                      decimal: true,
                    ),
                    decoration: InputDecoration(
                      labelText: l10n.docScanWidthMm,
                      errorText: _customWidth.text.isNotEmpty && !type.isValid
                          ? l10n.docScanCustomInvalid
                          : null,
                    ),
                    onChanged: (_) => setState(() {}),
                  ),
                ),
                const SizedBox(width: Spacing.x3),
                Expanded(
                  child: TextField(
                    controller: _customHeight,
                    keyboardType: const TextInputType.numberWithOptions(
                      decimal: true,
                    ),
                    decoration: InputDecoration(
                      labelText: l10n.docScanHeightMm,
                    ),
                    onChanged: (_) => setState(() {}),
                  ),
                ),
              ],
            ),
          ],
          const SizedBox(height: Spacing.x4),

          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            title: Text(l10n.docScanSensitive),
            subtitle: Text(
              type.alwaysSensitive
                  ? l10n.docScanSensitiveAlways
                  : l10n.docScanSensitiveHint,
            ),
            value: _sensitive,
            onChanged: type.alwaysSensitive
                ? null
                : (value) => setState(() => _sensitiveChosen = value),
          ),
          const SizedBox(height: Spacing.x4),

          Text(l10n.docScanArrangement, style: theme.textTheme.titleSmall),
          const SizedBox(height: Spacing.x2),
          Wrap(
            spacing: Spacing.x2,
            runSpacing: Spacing.x2,
            children: [
              for (final value in ScanArrangement.forType(type, _pages.length))
                ChoiceChip(
                  label: Text(_arrangementLabel(l10n, value)),
                  selected: _arrangement == value,
                  onSelected: (_) => setState(() {
                    _arrangement = value;
                    _arrangementChosen = true;
                  }),
                ),
            ],
          ),
          if (layout != null) ...[
            const SizedBox(height: Spacing.x3),
            SizedBox(
              height: 340,
              child: ListView.separated(
                scrollDirection: Axis.horizontal,
                itemCount: layout.pages.length,
                separatorBuilder: (_, __) => const SizedBox(width: Spacing.x3),
                itemBuilder: (context, index) => Column(
                  children: [
                    Expanded(
                      child: LayoutPagePreview(
                        project: layout,
                        page: layout.pages[index],
                        images: previews,
                      ),
                    ),
                    const SizedBox(height: Spacing.x1),
                    Text(
                      l10n.docScanPaperPage(index + 1, layout.pages.length),
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: tokens.neutral600,
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ],
          const SizedBox(height: Spacing.x4),

          TextField(
            controller: _name,
            decoration: InputDecoration(labelText: l10n.docScanFileName),
            onChanged: (_) => _nameEdited = true,
          ),
          const SizedBox(height: Spacing.x4),
          NeoBanner(tone: StatusTone.info, message: l10n.docScanLockedNote),
          if (_error case final error?) ...[
            const SizedBox(height: Spacing.x3),
            NeoBanner.error(message: error),
          ],
          const SizedBox(height: Spacing.x4),
          NeoPrimaryButton(
            label: _saving ? l10n.docScanSaving : l10n.docScanSave,
            busy: _saving,
            onPressed: canSave ? () => unawaited(_save()) : null,
          ),
        ],
      ),
    );
  }
}

/// Millimetres as typed, with a comma or a point; null when not a number.
double? parseMillimetres(String text) =>
    double.tryParse(text.trim().replaceAll(',', '.'));

class _PageThumb extends StatelessWidget {
  const _PageThumb({
    required this.label,
    required this.preview,
    required this.deleteLabel,
    required this.onTap,
    required this.onDelete,
  });

  final String label;
  final Uint8List? preview;
  final String deleteLabel;
  final VoidCallback onTap;
  final VoidCallback? onDelete;

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;

    return SizedBox(
      width: 100,
      child: Column(
        children: [
          Expanded(
            child: Stack(
              children: [
                Positioned.fill(
                  child: InkWell(
                    onTap: onTap,
                    borderRadius: BorderRadius.circular(Radii.sm),
                    child: DecoratedBox(
                      decoration: BoxDecoration(
                        border: Border.all(color: tokens.neutral200),
                        borderRadius: BorderRadius.circular(Radii.sm),
                      ),
                      child: switch (preview) {
                        final bytes? => Image.memory(
                          bytes,
                          fit: BoxFit.contain,
                          gaplessPlayback: true,
                        ),
                        null => const Center(
                          child: CircularProgressIndicator(),
                        ),
                      },
                    ),
                  ),
                ),
                if (onDelete != null)
                  Positioned(
                    top: 0,
                    right: 0,
                    child: IconButton(
                      tooltip: deleteLabel,
                      icon: Icon(Icons.delete_outline, color: tokens.danger),
                      onPressed: onDelete,
                    ),
                  ),
              ],
            ),
          ),
          const SizedBox(height: Spacing.x1),
          Text(label, style: Theme.of(context).textTheme.bodySmall),
        ],
      ),
    );
  }
}

class _AddPageCard extends StatelessWidget {
  const _AddPageCard({required this.label, required this.onTap});

  final String label;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;

    return SizedBox(
      width: 100,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(Radii.sm),
        child: DecoratedBox(
          decoration: BoxDecoration(
            border: Border.all(color: tokens.neutral200),
            borderRadius: BorderRadius.circular(Radii.sm),
          ),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Icon(Icons.add, color: tokens.brand500),
              const SizedBox(height: Spacing.x1),
              Text(
                label,
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.bodySmall,
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// No scanner on this phone (docs/DESIGN_SYSTEM.md: a warning, with the way on).
class _Unavailable extends StatelessWidget {
  const _Unavailable({required this.onCamera});

  final VoidCallback onCamera;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    return ListView(
      padding: const EdgeInsets.all(Spacing.x4),
      children: [
        NeoBanner(tone: StatusTone.warning, message: l10n.docScanUnavailable),
        const SizedBox(height: Spacing.x4),
        NeoPrimaryButton(
          label: l10n.filesTakePhoto,
          icon: Icons.photo_camera_outlined,
          onPressed: onCamera,
        ),
      ],
    );
  }
}
