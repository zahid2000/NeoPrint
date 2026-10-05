import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';
import 'layout_editing.dart';
import 'layout_model.dart';

/// How a gesture changes the picture it started on.
enum _Gesture { none, move, transform, resize }

/// The editor's canvas (plan section 2.11; docs/DESIGN_SYSTEM.md "Mobil:
/// səhifə tərtibatı redaktoru"): rulers in millimetres, the paper with its
/// margin and grid, the pictures, and the selection. One finger moves a
/// picture, two fingers scale and turn it, a corner handle scales it.
class LayoutCanvas extends StatefulWidget {
  const LayoutCanvas({
    super.key,
    required this.project,
    required this.page,
    required this.images,
    required this.selected,
    required this.lockRatio,
    required this.onSelect,
    required this.onChanged,
  });

  final LayoutProject project;
  final int page;

  /// Decoded pictures by name; a missing one is drawn as a grey box.
  final Map<String, ui.Image> images;
  final int? selected;
  final bool lockRatio;
  final ValueChanged<int?> onSelect;

  /// A picture changed; [done] when the finger is lifted.
  final void Function(int index, LayoutElement element, {required bool done})
  onChanged;

  @override
  State<LayoutCanvas> createState() => _LayoutCanvasState();
}

class _LayoutCanvasState extends State<LayoutCanvas> {
  static const _ruler = 24.0;
  static const _handleTouch = 28.0;

  _Gesture _gesture = _Gesture.none;
  LayoutElement? _start;
  Offset _startFocal = Offset.zero;
  List<SnapLine> _snapLines = const [];

  /// The paper's place on screen and its pixels per millimetre.
  (Rect, double) _paper(Size size) {
    final (width, height) = widget.project.pageSizeMm;
    final room = Size(size.width - _ruler - 16, size.height - _ruler - 16);
    final scale = math.min(room.width / width, room.height / height);
    final paperSize = Size(width * scale, height * scale);
    final left = _ruler + (size.width - _ruler - paperSize.width) / 2;
    final top = _ruler + (size.height - _ruler - paperSize.height) / 2;

    return (Offset(left, top) & paperSize, scale);
  }

  Offset _toMm(Offset point, Rect paper, double scale) =>
      (point - paper.topLeft) / scale;

  List<LayoutElement> get _elements =>
      widget.project.pages[widget.page].elements;

  /// The topmost picture under a point, if any.
  int? _hit(Offset mm) {
    for (var index = _elements.length - 1; index >= 0; index--) {
      if (_contains(_elements[index], mm)) {
        return index;
      }
    }
    return null;
  }

  static bool _contains(LayoutElement element, Offset mm) {
    final cx = element.xMm + element.widthMm / 2;
    final cy = element.yMm + element.heightMm / 2;
    final angle = -element.rotationDeg * math.pi / 180;
    final dx = mm.dx - cx;
    final dy = mm.dy - cy;
    final x = dx * math.cos(angle) - dy * math.sin(angle);
    final y = dx * math.sin(angle) + dy * math.cos(angle);

    return x.abs() <= element.widthMm / 2 && y.abs() <= element.heightMm / 2;
  }

  /// The selected picture's corners on screen.
  List<Offset> _corners(LayoutElement element, Rect paper, double scale) {
    final cx = element.xMm + element.widthMm / 2;
    final cy = element.yMm + element.heightMm / 2;
    final angle = element.rotationDeg * math.pi / 180;

    return [
      for (final (sx, sy) in const [(-1, -1), (1, -1), (1, 1), (-1, 1)])
        () {
          final dx = sx * element.widthMm / 2;
          final dy = sy * element.heightMm / 2;
          return paper.topLeft +
              Offset(
                    cx + dx * math.cos(angle) - dy * math.sin(angle),
                    cy + dx * math.sin(angle) + dy * math.cos(angle),
                  ) *
                  scale;
        }(),
    ];
  }

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;

    return LayoutBuilder(
      builder: (context, constraints) {
        final size = constraints.biggest;
        final (paper, scale) = _paper(size);

        return GestureDetector(
          behavior: HitTestBehavior.opaque,
          onScaleStart: (details) {
            final mm = _toMm(details.localFocalPoint, paper, scale);
            final selected = widget.selected;

            // A corner handle of the selected picture resizes it.
            if (selected != null &&
                details.pointerCount == 1 &&
                _corners(_elements[selected], paper, scale).any(
                  (corner) =>
                      (corner - details.localFocalPoint).distance <=
                      _handleTouch,
                )) {
              _gesture = _Gesture.resize;
              _start = _elements[selected];
              _startFocal = details.localFocalPoint;
              return;
            }

            final hit = _hit(mm);
            widget.onSelect(hit);

            if (hit == null) {
              _gesture = _Gesture.none;
              return;
            }

            _gesture = details.pointerCount > 1
                ? _Gesture.transform
                : _Gesture.move;
            _start = _elements[hit];
            _startFocal = details.localFocalPoint;
          },
          onScaleUpdate: (details) {
            final index = widget.selected;
            final start = _start;

            if (index == null || start == null || _gesture == _Gesture.none) {
              return;
            }

            if (details.pointerCount > 1 && _gesture == _Gesture.move) {
              // A second finger came down: scale and turn from here.
              _gesture = _Gesture.transform;
            }

            switch (_gesture) {
              case _Gesture.move:
                final delta = (details.localFocalPoint - _startFocal) / scale;
                final (moved, lines) = LayoutEditing.move(
                  widget.project,
                  widget.page,
                  index,
                  start,
                  delta.dx,
                  delta.dy,
                );
                setState(() => _snapLines = lines);
                widget.onChanged(index, moved, done: false);
              case _Gesture.transform:
                var changed = LayoutEditing.scale(
                  start,
                  details.scale,
                  details.scale,
                  lockRatio: true,
                );
                changed = LayoutEditing.rotate(
                  changed,
                  details.rotation * 180 / math.pi,
                );
                widget.onChanged(index, changed, done: false);
              case _Gesture.resize:
                final centre =
                    paper.topLeft +
                    Offset(
                          start.xMm + start.widthMm / 2,
                          start.yMm + start.heightMm / 2,
                        ) *
                        scale;
                final from = _startFocal - centre;
                final to = details.localFocalPoint - centre;
                final factorX = from.dx.abs() < 1 ? 1.0 : to.dx / from.dx;
                final factorY = from.dy.abs() < 1 ? 1.0 : to.dy / from.dy;
                final factor = to.distance / math.max(from.distance, 1);
                widget.onChanged(
                  index,
                  widget.lockRatio
                      ? LayoutEditing.scale(start, factor, factor)
                      : LayoutEditing.scale(
                          start,
                          factorX.abs(),
                          factorY.abs(),
                          lockRatio: false,
                        ),
                  done: false,
                );
              case _Gesture.none:
                break;
            }
          },
          onScaleEnd: (_) {
            final index = widget.selected;
            if (index != null && _gesture != _Gesture.none) {
              widget.onChanged(index, _elements[index], done: true);
            }
            setState(() {
              _gesture = _Gesture.none;
              _start = null;
              _snapLines = const [];
            });
          },
          child: CustomPaint(
            size: size,
            painter: _CanvasPainter(
              project: widget.project,
              page: widget.page,
              images: widget.images,
              selected: widget.selected,
              paper: paper,
              scale: scale,
              ruler: _ruler,
              showGrid: _gesture != _Gesture.none,
              snapLines: _snapLines,
              colours: _Colours(
                background: tokens.neutral100,
                paper: LightPalette.neutral0,
                paperEdge: tokens.neutral200,
                margin: tokens.neutral400,
                grid: tokens.neutral100,
                ruler: tokens.neutral600,
                selection: tokens.brand500,
                snap: tokens.brand500,
                warning: tokens.warning,
                placeholder: tokens.neutral200,
              ),
              textStyle: Theme.of(context).textTheme.bodySmall!.copyWith(
                color: tokens.neutral600,
                fontFeatures: const [ui.FontFeature.tabularFigures()],
              ),
            ),
          ),
        );
      },
    );
  }
}

final class _Colours {
  const _Colours({
    required this.background,
    required this.paper,
    required this.paperEdge,
    required this.margin,
    required this.grid,
    required this.ruler,
    required this.selection,
    required this.snap,
    required this.warning,
    required this.placeholder,
  });

  final Color background;
  final Color paper;
  final Color paperEdge;
  final Color margin;
  final Color grid;
  final Color ruler;
  final Color selection;
  final Color snap;
  final Color warning;
  final Color placeholder;
}

class _CanvasPainter extends CustomPainter {
  const _CanvasPainter({
    required this.project,
    required this.page,
    required this.images,
    required this.selected,
    required this.paper,
    required this.scale,
    required this.ruler,
    required this.showGrid,
    required this.snapLines,
    required this.colours,
    required this.textStyle,
  });

  final LayoutProject project;
  final int page;
  final Map<String, ui.Image> images;
  final int? selected;
  final Rect paper;
  final double scale;
  final double ruler;
  final bool showGrid;
  final List<SnapLine> snapLines;
  final _Colours colours;
  final TextStyle textStyle;

  @override
  void paint(Canvas canvas, Size size) {
    canvas.drawRect(Offset.zero & size, Paint()..color = colours.background);
    _rulers(canvas, size);

    canvas
      ..drawRect(paper, Paint()..color = colours.paper)
      ..drawRect(
        paper,
        Paint()
          ..style = PaintingStyle.stroke
          ..color = colours.paperEdge,
      );

    canvas
      ..save()
      ..clipRect(paper);

    if (showGrid) {
      final grid = Paint()..color = colours.grid;
      final (width, height) = project.pageSizeMm;
      for (
        var mm = LayoutEditing.gridMm;
        mm < width;
        mm += LayoutEditing.gridMm
      ) {
        canvas.drawLine(_p(mm, 0), _p(mm, height), grid);
      }
      for (
        var mm = LayoutEditing.gridMm;
        mm < height;
        mm += LayoutEditing.gridMm
      ) {
        canvas.drawLine(_p(0, mm), _p(width, mm), grid);
      }
    }

    _margin(canvas);

    final elements = project.pages[page].elements;
    for (final element in elements) {
      _element(canvas, element);
    }

    for (final line in snapLines) {
      final (width, height) = project.pageSizeMm;
      final paint = Paint()
        ..color = colours.snap
        ..strokeWidth = 1;
      if (line.vertical) {
        _dashed(canvas, _p(line.mm, 0), _p(line.mm, height), paint);
      } else {
        _dashed(canvas, _p(0, line.mm), _p(width, line.mm), paint);
      }
    }

    canvas.restore();

    if (selected case final index? when index < elements.length) {
      _selection(canvas, elements[index]);
    }
  }

  Offset _p(double xMm, double yMm) =>
      paper.topLeft + Offset(xMm * scale, yMm * scale);

  void _element(Canvas canvas, LayoutElement element) {
    final rect = Rect.fromCenter(
      center: Offset.zero,
      width: element.widthMm * scale,
      height: element.heightMm * scale,
    );

    canvas
      ..save()
      ..translate(
        paper.left + (element.xMm + element.widthMm / 2) * scale,
        paper.top + (element.yMm + element.heightMm / 2) * scale,
      )
      ..rotate(element.rotationDeg * math.pi / 180);

    switch (images[element.image]) {
      case final image?:
        canvas.drawImageRect(
          image,
          Rect.fromLTWH(0, 0, image.width.toDouble(), image.height.toDouble()),
          rect,
          Paint()..filterQuality = FilterQuality.medium,
        );
      case null:
        canvas.drawRect(rect, Paint()..color = colours.placeholder);
    }

    canvas.restore();
  }

  void _selection(Canvas canvas, LayoutElement element) {
    final warn = LayoutGeometry.leavesPrintableArea(project, element);
    final colour = warn ? colours.warning : colours.selection;
    final rect = Rect.fromCenter(
      center: Offset.zero,
      width: element.widthMm * scale,
      height: element.heightMm * scale,
    );

    canvas
      ..save()
      ..translate(
        paper.left + (element.xMm + element.widthMm / 2) * scale,
        paper.top + (element.yMm + element.heightMm / 2) * scale,
      )
      ..rotate(element.rotationDeg * math.pi / 180)
      ..drawRect(
        rect,
        Paint()
          ..style = PaintingStyle.stroke
          ..strokeWidth = 2
          ..color = colour,
      );

    for (final corner in [
      rect.topLeft,
      rect.topRight,
      rect.bottomRight,
      rect.bottomLeft,
    ]) {
      canvas
        ..drawCircle(corner, 10, Paint()..color = colours.paper)
        ..drawCircle(
          corner,
          10,
          Paint()
            ..style = PaintingStyle.stroke
            ..strokeWidth = 2
            ..color = colour,
        );
    }

    canvas.restore();
  }

  void _margin(Canvas canvas) {
    final (width, height) = project.pageSizeMm;
    final m = project.marginMm;
    if (m <= 0) {
      return;
    }
    final paint = Paint()
      ..color = colours.margin
      ..strokeWidth = 1;
    _dashed(canvas, _p(m, m), _p(width - m, m), paint);
    _dashed(canvas, _p(m, height - m), _p(width - m, height - m), paint);
    _dashed(canvas, _p(m, m), _p(m, height - m), paint);
    _dashed(canvas, _p(width - m, m), _p(width - m, height - m), paint);
  }

  void _rulers(Canvas canvas, Size size) {
    final (width, height) = project.pageSizeMm;
    final paint = Paint()
      ..color = colours.ruler
      ..strokeWidth = 1;
    // Millimetre ticks only when they are far enough apart to see.
    final everyMm = scale >= 3 ? 1.0 : 5.0;

    for (var mm = 0.0; mm <= width; mm += everyMm) {
      final x = paper.left + mm * scale;
      final long = mm % 10 == 0 ? 10.0 : (mm % 5 == 0 ? 6.0 : 3.0);
      canvas.drawLine(Offset(x, ruler), Offset(x, ruler - long), paint);
      if (mm % 50 == 0) {
        _label(canvas, '${mm.round()}', Offset(x + 2, 0));
      }
    }

    for (var mm = 0.0; mm <= height; mm += everyMm) {
      final y = paper.top + mm * scale;
      final long = mm % 10 == 0 ? 10.0 : (mm % 5 == 0 ? 6.0 : 3.0);
      canvas.drawLine(Offset(ruler, y), Offset(ruler - long, y), paint);
      if (mm % 50 == 0 && mm > 0) {
        _label(canvas, '${mm.round()}', Offset(0, y + 2));
      }
    }
  }

  void _label(Canvas canvas, String text, Offset at) {
    final painter = TextPainter(
      text: TextSpan(text: text, style: textStyle),
      textDirection: TextDirection.ltr,
    )..layout();
    painter.paint(canvas, at);
  }

  static void _dashed(Canvas canvas, Offset from, Offset to, Paint paint) {
    const dash = 4.0;
    final total = (to - from).distance;
    if (total == 0) {
      return;
    }
    final step = (to - from) / total;
    for (var d = 0.0; d < total; d += dash * 2) {
      canvas.drawLine(
        from + step * d,
        from + step * math.min(d + dash, total),
        paint,
      );
    }
  }

  @override
  bool shouldRepaint(_CanvasPainter old) =>
      old.project != project ||
      old.page != page ||
      old.images != images ||
      old.selected != selected ||
      old.paper != paper ||
      old.showGrid != showGrid ||
      old.snapLines != snapLines;
}
