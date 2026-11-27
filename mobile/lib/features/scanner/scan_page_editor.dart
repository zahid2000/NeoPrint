import 'dart:io';

import 'package:flutter/foundation.dart' show listEquals;
import 'package:material_ui/material_ui.dart';

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
                  // Not in a row: the theme's buttons are full width, and an
                  // unbounded one laid out nothing - this whole panel with it.
                  OutlinedButton.icon(
                    key: const ValueKey('scanEditRotate'),
                    onPressed: _rotate,
                    icon: const Icon(Icons.rotate_right),
                    label: Text(l10n.docScanEditRotate),
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

/// The turned page with the crop over it (T-476): four points, one at each
/// corner, moved one by one - the page need not be a rectangle - and one in
/// the middle of each side, which moves that side. While a point is held, a
/// magnifier above the finger shows the corner it is on.
class _CropView extends StatefulWidget {
  const _CropView({
    required this.page,
    required this.edit,
    required this.onCrop,
  });

  final ScanPage page;
  final ScanPageEdit edit;
  final ValueChanged<ScanCrop> onCrop;

  @override
  State<_CropView> createState() => _CropViewState();
}

class _CropViewState extends State<_CropView> {
  /// The touch area of a point, and the dot drawn in it.
  static const _touch = Spacing.x12;
  static const _corner = Spacing.x6;
  static const _edge = Spacing.x4;

  /// The magnifier: its size, how much it enlarges, and how far above the
  /// finger it sits so the finger does not cover it.
  static const _loupe = Spacing.x16 + Spacing.x8;
  static const _zoom = 2.0;
  static const _lift = Spacing.x16 + Spacing.x2;

  /// The point being dragged, where it is on screen.
  Offset? _held;

  @override
  Widget build(BuildContext context) {
    final edit = widget.edit;
    final page = widget.page;
    final turned = edit.quarterTurns.isOdd;
    final aspect = turned ? page.height / page.width : page.width / page.height;

    // The page with room around it for half a point: a touch is taken only
    // inside the area, and the points sit on the page's edges - without the
    // room, the outer half of the right and bottom ones did not answer.
    return LayoutBuilder(
      builder: (context, constraints) {
        const margin = _touch / 2;
        final room = Size(
          constraints.maxWidth - 2 * margin,
          constraints.maxHeight - 2 * margin,
        );
        final size = room.width / room.height > aspect
            ? Size(room.height * aspect, room.height)
            : Size(room.width, room.width / aspect);
        final origin = Offset(
          (constraints.maxWidth - size.width) / 2,
          (constraints.maxHeight - size.height) / 2,
        );
        final picture = origin & size;
        final crop = edit.crop;
        Offset at(ScanPoint p) =>
            origin + Offset(p.x * size.width, p.y * size.height);
        final corners = [for (final p in crop.corners) at(p)];

        // A move is taken only when the shape stays one that can be
        // straightened: no side crossing another, no sliver.
        void move(ScanCrop next) {
          final inside = next.clamped();
          if (inside.isUsable) {
            widget.onCrop(inside);
          }
        }

        ScanPoint shifted(ScanPoint p, Offset delta) =>
            (x: p.x + delta.dx / size.width, y: p.y + delta.dy / size.height);

        Widget handle({
          required Key key,
          required Offset centre,
          required double dot,
          required void Function(Offset delta) onDrag,
        }) => Positioned(
          left: centre.dx - _touch / 2,
          top: centre.dy - _touch / 2,
          child: GestureDetector(
            key: key,
            behavior: HitTestBehavior.opaque,
            onPanStart: (_) => setState(() => _held = centre),
            onPanUpdate: (details) {
              onDrag(details.delta);
              setState(() => _held = (_held ?? centre) + details.delta);
            },
            onPanEnd: (_) => setState(() => _held = null),
            onPanCancel: () => setState(() => _held = null),
            child: SizedBox.square(
              dimension: _touch,
              child: Center(
                child: Container(
                  width: dot,
                  height: dot,
                  decoration: BoxDecoration(
                    color: LightPalette.neutral0,
                    shape: BoxShape.circle,
                    border: Border.all(color: LightPalette.brand500, width: 2),
                  ),
                ),
              ),
            ),
          ),
        );

        final held = _held;
        return Stack(
          children: [
            Positioned.fromRect(
              rect: picture,
              child: ColorFiltered(
                colorFilter: ColorFilter.matrix(_matrix(edit)),
                child: RotatedBox(
                  quarterTurns: edit.quarterTurns,
                  child: Image.file(File(page.path), fit: BoxFit.fill),
                ),
              ),
            ),
            Positioned.fill(
              child: CustomPaint(painter: _ShadePainter(picture, corners)),
            ),
            // The middle of each side moves the side: both its ends.
            for (var i = 0; i < 4; i++)
              handle(
                key: ValueKey('scanCropEdge-$i'),
                centre: (corners[i] + corners[(i + 1) % 4]) / 2,
                dot: _edge,
                onDrag: (delta) {
                  final c = crop.corners;
                  move(
                    crop
                        .withCorner(i, shifted(c[i], delta))
                        .withCorner(
                          (i + 1) % 4,
                          shifted(c[(i + 1) % 4], delta),
                        ),
                  );
                },
              ),
            for (var i = 0; i < 4; i++)
              handle(
                key: ValueKey('scanCropCorner-$i'),
                centre: corners[i],
                dot: _corner,
                onDrag: (delta) =>
                    move(crop.withCorner(i, shifted(crop.corners[i], delta))),
              ),
            if (held != null)
              Positioned(
                left: held.dx - _loupe / 2,
                top: held.dy - _loupe / 2 - _lift,
                child: const IgnorePointer(
                  child: RawMagnifier(
                    size: Size.square(_loupe),
                    magnificationScale: _zoom,
                    focalPointOffset: Offset(0, _lift),
                    decoration: MagnifierDecoration(
                      shape: CircleBorder(
                        side: BorderSide(
                          color: LightPalette.neutral0,
                          width: 2,
                        ),
                      ),
                    ),
                  ),
                ),
              ),
          ],
        );
      },
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

/// Darkens everything outside the crop and draws its sides (T-476).
class _ShadePainter extends CustomPainter {
  const _ShadePainter(this.picture, this.corners);

  /// Where the page is drawn.
  final Rect picture;

  /// Clockwise from the top left, on screen.
  final List<Offset> corners;

  @override
  void paint(Canvas canvas, Size size) {
    final shape = Path()..addPolygon(corners, true);
    final outside = Path()
      ..fillType = PathFillType.evenOdd
      ..addRect(picture)
      ..addPath(shape, Offset.zero);

    canvas
      ..drawPath(
        outside,
        Paint()..color = LightPalette.neutral900.withValues(alpha: 0.5),
      )
      ..drawPath(
        shape,
        Paint()
          ..style = PaintingStyle.stroke
          ..strokeWidth = 2
          ..color = LightPalette.brand500,
      );
  }

  @override
  bool shouldRepaint(_ShadePainter old) =>
      old.picture != picture || !listEquals(old.corners, corners);
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
