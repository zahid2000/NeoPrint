import 'dart:math' as math;
import 'dart:typed_data';

import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';
import 'layout_model.dart';

/// One page of a layout as it will print: white paper, the margin dashed,
/// each picture in its box at scale (docs/DESIGN_SYSTEM.md "Mobil: sənəd
/// skanı", "Kağızda yerləşmə").
class LayoutPagePreview extends StatelessWidget {
  const LayoutPagePreview({
    super.key,
    required this.project,
    required this.page,
    required this.images,
  });

  final LayoutProject project;
  final LayoutPage page;

  /// Pictures by name; one still being prepared shows as a grey box.
  final Map<String, Uint8List> images;

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;
    final (paperWidth, paperHeight) = project.pageSizeMm;

    return AspectRatio(
      aspectRatio: paperWidth / paperHeight,
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: LightPalette.neutral0,
          border: Border.all(color: tokens.neutral200),
        ),
        child: LayoutBuilder(
          builder: (context, constraints) {
            final scale = constraints.maxWidth / paperWidth;

            return Stack(
              clipBehavior: Clip.hardEdge,
              children: [
                Positioned.fill(
                  child: CustomPaint(
                    painter: _MarginPainter(
                      project.marginMm * scale,
                      tokens.neutral400,
                    ),
                  ),
                ),
                for (final element in page.elements)
                  Positioned(
                    left: element.xMm * scale,
                    top: element.yMm * scale,
                    width: element.widthMm * scale,
                    height: element.heightMm * scale,
                    child: Transform.rotate(
                      angle: element.rotationDeg * math.pi / 180,
                      child: switch (images[element.image]) {
                        final bytes? => Image.memory(
                          bytes,
                          fit: BoxFit.fill,
                          gaplessPlayback: true,
                        ),
                        null => ColoredBox(color: tokens.neutral200),
                      },
                    ),
                  ),
              ],
            );
          },
        ),
      ),
    );
  }
}

/// The margin the editor keeps to, as a dashed line.
class _MarginPainter extends CustomPainter {
  const _MarginPainter(this.margin, this.color);

  final double margin;
  final Color color;

  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = color
      ..strokeWidth = 1;
    final rect = Rect.fromLTRB(
      margin,
      margin,
      size.width - margin,
      size.height - margin,
    );
    const dash = 4.0;

    for (var x = rect.left; x < rect.right; x += dash * 2) {
      final end = math.min(x + dash, rect.right);
      canvas
        ..drawLine(Offset(x, rect.top), Offset(end, rect.top), paint)
        ..drawLine(Offset(x, rect.bottom), Offset(end, rect.bottom), paint);
    }

    for (var y = rect.top; y < rect.bottom; y += dash * 2) {
      final end = math.min(y + dash, rect.bottom);
      canvas
        ..drawLine(Offset(rect.left, y), Offset(rect.left, end), paint)
        ..drawLine(Offset(rect.right, y), Offset(rect.right, end), paint);
    }
  }

  @override
  bool shouldRepaint(_MarginPainter old) =>
      old.margin != margin || old.color != color;
}
