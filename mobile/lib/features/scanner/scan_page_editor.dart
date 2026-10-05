import 'dart:io';

import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_primary_button.dart';
import 'scan_models.dart';
import 'scan_processing.dart';

/// One page after the scanner: crop, turn, filter, brightness (plan section
/// 2.11; docs/DESIGN_SYSTEM.md "Mobil: sənəd skanı", "Səhifə redaktoru").
///
/// What is shown is a preview - the turn and crop exactly, the filter and
/// brightness as a colour matrix. The page itself is processed once, when
/// the scan is saved. Returns the edit, or null when left unchanged.
class ScanPageEditor extends StatefulWidget {
  const ScanPageEditor({super.key, required this.page, required this.number});

  final ScanPage page;

  /// One-based, for the title.
  final int number;

  @override
  State<ScanPageEditor> createState() => _ScanPageEditorState();
}

class _ScanPageEditorState extends State<ScanPageEditor> {
  late ScanPageEdit _edit = widget.page.edit;

  /// The crop is held for the picture as turned; turning resets it, as a
  /// crop for the old orientation would cut the wrong part.
  void _rotate() => setState(
    () => _edit = _edit.copyWith(
      quarterTurns: (_edit.quarterTurns + 1) % 4,
      crop: ScanCrop.whole,
    ),
  );

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final tokens = context.tokens;

    return Scaffold(
      // A photo is judged on a dark ground in either theme.
      backgroundColor: LightPalette.neutral900,
      appBar: AppBar(
        backgroundColor: LightPalette.neutral900,
        foregroundColor: LightPalette.neutral0,
        title: Text(l10n.docScanPageN(widget.number)),
        actions: [
          TextButton(
            onPressed: () => setState(() => _edit = const ScanPageEdit()),
            child: Text(
              l10n.docScanEditReset,
              style: const TextStyle(color: LightPalette.neutral0),
            ),
          ),
        ],
      ),
      body: SafeArea(
        child: Column(
          children: [
            Expanded(
              child: Padding(
                padding: const EdgeInsets.all(Spacing.x6),
                child: _CropView(
                  page: widget.page,
                  edit: _edit,
                  onCrop: (crop) =>
                      setState(() => _edit = _edit.copyWith(crop: crop)),
                ),
              ),
            ),
            Container(
              color: tokens.neutral0,
              padding: const EdgeInsets.all(Spacing.x4),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Row(
                    children: [
                      OutlinedButton.icon(
                        onPressed: _rotate,
                        icon: const Icon(Icons.rotate_right),
                        label: Text(l10n.docScanEditRotate),
                      ),
                    ],
                  ),
                  const SizedBox(height: Spacing.x3),
                  SegmentedButton<ScanFilter>(
                    showSelectedIcon: false,
                    segments: [
                      ButtonSegment(
                        value: ScanFilter.original,
                        label: Text(l10n.docScanFilterOriginal),
                      ),
                      ButtonSegment(
                        value: ScanFilter.grayscale,
                        label: Text(l10n.docScanFilterGray),
                      ),
                      ButtonSegment(
                        value: ScanFilter.blackWhite,
                        label: Text(l10n.docScanFilterBw),
                      ),
                      ButtonSegment(
                        value: ScanFilter.contrast,
                        label: Text(l10n.docScanFilterContrast),
                      ),
                    ],
                    selected: {_edit.filter},
                    onSelectionChanged: (value) => setState(
                      () => _edit = _edit.copyWith(filter: value.single),
                    ),
                  ),
                  const SizedBox(height: Spacing.x3),
                  Text(
                    l10n.docScanBrightness,
                    style: Theme.of(context).textTheme.titleSmall,
                  ),
                  Slider(
                    min: -100,
                    max: 100,
                    divisions: 40,
                    value: _edit.brightness.toDouble(),
                    label: '${_edit.brightness}',
                    onChanged: (value) => setState(
                      () => _edit = _edit.copyWith(brightness: value.round()),
                    ),
                  ),
                  const SizedBox(height: Spacing.x2),
                  NeoPrimaryButton(
                    label: l10n.docScanEditDone,
                    onPressed: () => Navigator.pop(context, _edit),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// The turned page with the crop frame over it.
class _CropView extends StatelessWidget {
  const _CropView({
    required this.page,
    required this.edit,
    required this.onCrop,
  });

  final ScanPage page;
  final ScanPageEdit edit;
  final ValueChanged<ScanCrop> onCrop;

  static const _handle = 24.0;

  @override
  Widget build(BuildContext context) {
    final turned = edit.quarterTurns.isOdd;
    final aspect = turned ? page.height / page.width : page.width / page.height;

    return Center(
      child: AspectRatio(
        aspectRatio: aspect,
        child: LayoutBuilder(
          builder: (context, constraints) {
            final width = constraints.maxWidth;
            final height = constraints.maxHeight;
            final crop = edit.crop;
            final frame = Rect.fromLTRB(
              crop.left * width,
              crop.top * height,
              crop.right * width,
              crop.bottom * height,
            );

            void drag(Alignment corner, Offset delta) {
              final dx = delta.dx / width;
              final dy = delta.dy / height;
              onCrop(
                ScanCrop(
                  crop.left + (corner.x < 0 ? dx : 0),
                  crop.top + (corner.y < 0 ? dy : 0),
                  crop.right + (corner.x > 0 ? dx : 0),
                  crop.bottom + (corner.y > 0 ? dy : 0),
                ).clamped(),
              );
            }

            return Stack(
              clipBehavior: Clip.none,
              children: [
                Positioned.fill(
                  child: ColorFiltered(
                    colorFilter: ColorFilter.matrix(_matrix(edit)),
                    child: RotatedBox(
                      quarterTurns: edit.quarterTurns,
                      child: Image.file(File(page.path), fit: BoxFit.fill),
                    ),
                  ),
                ),
                Positioned.fill(
                  child: CustomPaint(painter: _ShadePainter(frame)),
                ),
                for (final corner in const [
                  Alignment.topLeft,
                  Alignment.topRight,
                  Alignment.bottomLeft,
                  Alignment.bottomRight,
                ])
                  Positioned(
                    left: (corner.x < 0 ? frame.left : frame.right) - _handle,
                    top: (corner.y < 0 ? frame.top : frame.bottom) - _handle,
                    child: GestureDetector(
                      behavior: HitTestBehavior.opaque,
                      onPanUpdate: (details) => drag(corner, details.delta),
                      child: SizedBox(
                        width: _handle * 2,
                        height: _handle * 2,
                        child: Center(
                          child: Container(
                            width: _handle,
                            height: _handle,
                            decoration: BoxDecoration(
                              color: LightPalette.neutral0,
                              shape: BoxShape.circle,
                            ),
                          ),
                        ),
                      ),
                    ),
                  ),
              ],
            );
          },
        ),
      ),
    );
  }

  /// The filter and brightness as a colour matrix, close to what
  /// [ScanProcessing] does to the pixels.
  static List<double> _matrix(ScanPageEdit edit) {
    final b = 1 + edit.brightness.clamp(-100, 100) / 100;
    const r = 0.299, g = 0.587, bl = 0.114;

    final base = switch (edit.filter) {
      ScanFilter.original => [1.0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0],
      ScanFilter.grayscale ||
      ScanFilter.blackWhite => [r, g, bl, 0, 0, r, g, bl, 0, 0, r, g, bl, 0, 0],
      ScanFilter.contrast => [
        1.5,
        0,
        0,
        0,
        -32,
        0,
        1.5,
        0,
        0,
        -32,
        0,
        0,
        1.5,
        0,
        -32,
      ],
    };

    // Black and white: push the grey hard to either end.
    final hard = edit.filter == ScanFilter.blackWhite ? 6.0 : 1.0;
    final offset = edit.filter == ScanFilter.blackWhite ? -640.0 : 0.0;

    return [
      for (var row = 0; row < 3; row++) ...[
        for (var column = 0; column < 4; column++)
          base[row * 5 + column].toDouble() * b * hard,
        base[row * 5 + 4].toDouble() * b + offset,
      ],
      0,
      0,
      0,
      1,
      0,
    ];
  }
}

/// Darkens everything outside the crop frame and draws the frame.
class _ShadePainter extends CustomPainter {
  const _ShadePainter(this.frame);

  final Rect frame;

  @override
  void paint(Canvas canvas, Size size) {
    final outside = Path()
      ..fillType = PathFillType.evenOdd
      ..addRect(Offset.zero & size)
      ..addRect(frame);

    canvas
      ..drawPath(
        outside,
        Paint()..color = LightPalette.neutral900.withValues(alpha: 0.5),
      )
      ..drawRect(
        frame,
        Paint()
          ..style = PaintingStyle.stroke
          ..strokeWidth = 2
          ..color = LightPalette.neutral0,
      );
  }

  @override
  bool shouldRepaint(_ShadePainter old) => old.frame != frame;
}

/// Opens the editor for a page and returns its new edit, or null.
Future<ScanPageEdit?> editScanPage(
  BuildContext context,
  ScanPage page,
  int number,
) => Navigator.of(context).push<ScanPageEdit>(
  MaterialPageRoute(
    fullscreenDialog: true,
    builder: (_) => ScanPageEditor(page: page, number: number),
  ),
);
