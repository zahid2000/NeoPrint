import 'dart:math' as math;

import 'layout_model.dart';

/// A picture the editor can place, with what is known about it.
final class EditorPicture {
  const EditorPicture({
    required this.name,
    required this.aspect,
    this.realWidthMm,
    this.realHeightMm,
  });

  /// Its name in the layout.
  final String name;

  /// Width over height of the picture as it is.
  final double aspect;

  /// The document's real size, when it is known (a scan's document type).
  final double? realWidthMm;
  final double? realHeightMm;

  bool get hasRealSize => realWidthMm != null && realHeightMm != null;
}

/// A line the moved picture snapped to, for the editor to show.
final class SnapLine {
  const SnapLine.vertical(this.mm) : vertical = true;
  const SnapLine.horizontal(this.mm) : vertical = false;

  final bool vertical;
  final double mm;

  @override
  bool operator ==(Object other) =>
      other is SnapLine && other.vertical == vertical && other.mm == mm;

  @override
  int get hashCode => Object.hash(vertical, mm);
}

/// The editor's operations on a layout (plan section 2.11), each returning
/// a new layout. Pure, so the coordinates can be tested to the hundredth.
abstract final class LayoutEditing {
  /// How close an edge or centre must come to snap.
  static const snapMm = 2.0;

  /// How close an angle must come to a right angle to snap.
  static const angleSnapDeg = 3.0;

  /// The editor's grid.
  static const gridMm = 5.0;

  /// The size limits the backend checks (`LayoutJson`).
  static const minSideMm = 1.0;
  static const maxSideMm = 1000.0;

  /// Where a copy lands relative to the original.
  static const duplicateOffsetMm = 5.0;

  static LayoutProject replace(
    LayoutProject project,
    int page,
    int index,
    LayoutElement element,
  ) => _withPage(project, page, [
    for (final (i, current) in project.pages[page].elements.indexed)
      i == index ? element : current,
  ]);

  /// Moves a picture by the given distance and snaps it to the paper's edges,
  /// margin and centre and to the other pictures (within [snapMm]).
  static (LayoutElement, List<SnapLine>) move(
    LayoutProject project,
    int page,
    int index,
    LayoutElement start,
    double dxMm,
    double dyMm, {
    bool snap = true,
  }) {
    var moved = start.copyWith(xMm: start.xMm + dxMm, yMm: start.yMm + dyMm);

    if (!snap) {
      return (moved, const []);
    }

    final (width, height) = project.pageSizeMm;
    final margin = project.marginMm;
    final others = [
      for (final (i, element) in project.pages[page].elements.indexed)
        if (i != index) LayoutGeometry.bounds(element),
    ];
    final xTargets = <double>[
      0,
      margin,
      width / 2,
      width - margin,
      width,
      for (final box in others) ...[
        box.left,
        box.right,
        (box.left + box.right) / 2,
      ],
    ];
    final yTargets = <double>[
      0,
      margin,
      height / 2,
      height - margin,
      height,
      for (final box in others) ...[
        box.top,
        box.bottom,
        (box.top + box.bottom) / 2,
      ],
    ];

    final box = LayoutGeometry.bounds(moved);
    final lines = <SnapLine>[];

    final dx = _nearest([
      box.left,
      box.right,
      (box.left + box.right) / 2,
    ], xTargets);
    if (dx != null) {
      moved = moved.copyWith(xMm: moved.xMm + dx.$1);
      lines.add(SnapLine.vertical(dx.$2));
    }

    final dy = _nearest([
      box.top,
      box.bottom,
      (box.top + box.bottom) / 2,
    ], yTargets);
    if (dy != null) {
      moved = moved.copyWith(yMm: moved.yMm + dy.$1);
      lines.add(SnapLine.horizontal(dy.$2));
    }

    return (_rounded(moved), lines);
  }

  /// The smallest correction that puts one of [edges] on one of [targets],
  /// with the target, or null when none is within [snapMm].
  static (double, double)? _nearest(List<double> edges, List<double> targets) {
    (double, double)? best;

    for (final edge in edges) {
      for (final target in targets) {
        final distance = target - edge;
        if (distance.abs() <= snapMm &&
            (best == null || distance.abs() < best.$1.abs())) {
          best = (distance, target);
        }
      }
    }

    return best;
  }

  /// Scales a picture about its centre. With [lockRatio], by one factor -
  /// the larger change of the two - so the picture keeps its shape.
  static LayoutElement scale(
    LayoutElement start,
    double factorX,
    double factorY, {
    bool lockRatio = true,
  }) {
    var fx = factorX;
    var fy = factorY;

    if (lockRatio) {
      final f = (fx - 1).abs() >= (fy - 1).abs() ? fx : fy;
      fx = f;
      fy = f;
    }

    // Keep both sides inside the backend's limits, and the shape when locked.
    var width = start.widthMm * fx;
    var height = start.heightMm * fy;
    final shrink = math.max(
      math.max(minSideMm / width, minSideMm / height),
      1.0,
    );
    final grow = math.min(math.min(maxSideMm / width, maxSideMm / height), 1.0);
    if (lockRatio) {
      width *= shrink * grow;
      height *= shrink * grow;
    } else {
      width = width.clamp(minSideMm, maxSideMm);
      height = height.clamp(minSideMm, maxSideMm);
    }

    return _rounded(_centred(start, width, height));
  }

  /// Turns a picture by [deltaDeg]; within [angleSnapDeg] of a right angle
  /// it settles on it.
  static LayoutElement rotate(LayoutElement start, double deltaDeg) {
    var angle = _normalise(start.rotationDeg + deltaDeg);
    final nearest = (angle / 90).round() * 90.0;

    if ((angle - nearest).abs() <= angleSnapDeg) {
      angle = _normalise(nearest);
    }

    return start.copyWith(rotationDeg: _round(angle));
  }

  /// A quarter turn clockwise, to the next right angle.
  static LayoutElement rotate90(LayoutElement element) => element.copyWith(
    rotationDeg: _normalise(((element.rotationDeg / 90).floor() + 1) * 90.0),
  );

  /// Back to the document's real size, held the way the picture is now
  /// (plan section 2.11, "Real ölçüyə qaytar").
  static LayoutElement realSize(LayoutElement element, EditorPicture picture) {
    if (!picture.hasRealSize) {
      return element;
    }

    final long = math.max(picture.realWidthMm!, picture.realHeightMm!);
    final short = math.min(picture.realWidthMm!, picture.realHeightMm!);
    final landscape = element.widthMm >= element.heightMm;

    return _rounded(
      _centred(element, landscape ? long : short, landscape ? short : long),
    );
  }

  /// A copy of the picture just below and right of it, on top of the others.
  static LayoutProject duplicate(LayoutProject project, int page, int index) {
    final element = project.pages[page].elements[index];

    return _withPage(project, page, [
      ...project.pages[page].elements,
      element.copyWith(
        xMm: element.xMm + duplicateOffsetMm,
        yMm: element.yMm + duplicateOffsetMm,
      ),
    ]);
  }

  static LayoutProject delete(LayoutProject project, int page, int index) =>
      _withPage(project, page, [
        for (final (i, element) in project.pages[page].elements.indexed)
          if (i != index) element,
      ]);

  /// To the front: drawn last.
  static LayoutProject toFront(LayoutProject project, int page, int index) {
    final elements = [...project.pages[page].elements];
    elements.add(elements.removeAt(index));
    return _withPage(project, page, elements);
  }

  /// To the back: drawn first.
  static LayoutProject toBack(LayoutProject project, int page, int index) {
    final elements = [...project.pages[page].elements];
    elements.insert(0, elements.removeAt(index));
    return _withPage(project, page, elements);
  }

  /// In the middle of the paper, both ways.
  static LayoutElement centre(LayoutProject project, LayoutElement element) {
    final (width, height) = project.pageSizeMm;

    return _rounded(
      element.copyWith(
        xMm: (width - element.widthMm) / 2,
        yMm: (height - element.heightMm) / 2,
      ),
    );
  }

  /// A new picture in the middle of the page: at real size when known,
  /// otherwise as large as fits inside the margins.
  static LayoutProject add(
    LayoutProject project,
    int page,
    EditorPicture picture,
  ) {
    final (width, height) = project.pageSizeMm;
    final usableWidth = width - 2 * project.marginMm;
    final usableHeight = height - 2 * project.marginMm;
    double w;
    double h;

    if (picture.hasRealSize) {
      w = picture.realWidthMm!;
      h = picture.realHeightMm!;
    } else {
      w = usableWidth;
      h = usableWidth / picture.aspect;
    }

    final fit = math.min(1.0, math.min(usableWidth / w, usableHeight / h));
    final element = centre(
      project,
      LayoutElement(
        image: picture.name,
        xMm: 0,
        yMm: 0,
        widthMm: w * fit,
        heightMm: h * fit,
      ),
    );

    return _withPage(project, page, [...project.pages[page].elements, element]);
  }

  /// Pictures whose centre is off the paper: the backend refuses them.
  static bool hasOffPage(LayoutProject project) {
    final (width, height) = project.pageSizeMm;

    return project.pages.any(
      (page) => page.elements.any((element) {
        final x = element.xMm + element.widthMm / 2;
        final y = element.yMm + element.heightMm / 2;
        return x < 0 || y < 0 || x > width || y > height;
      }),
    );
  }

  /// Whether any picture reaches where a printer cannot print (a warning).
  static bool leavesPrintableArea(LayoutProject project) => project.pages.any(
    (page) => page.elements.any(
      (element) => LayoutGeometry.leavesPrintableArea(project, element),
    ),
  );

  static LayoutElement _centred(
    LayoutElement element,
    double width,
    double height,
  ) {
    final cx = element.xMm + element.widthMm / 2;
    final cy = element.yMm + element.heightMm / 2;

    return element.copyWith(
      xMm: cx - width / 2,
      yMm: cy - height / 2,
      widthMm: width,
      heightMm: height,
    );
  }

  static LayoutElement _rounded(LayoutElement element) => element.copyWith(
    xMm: _round(element.xMm),
    yMm: _round(element.yMm),
    widthMm: _round(element.widthMm),
    heightMm: _round(element.heightMm),
  );

  static double _round(double value) => LayoutGeometry.round(value);

  static double _normalise(double degrees) {
    final value = degrees % 360;
    return value < 0 ? value + 360 : value;
  }

  static LayoutProject _withPage(
    LayoutProject project,
    int page,
    List<LayoutElement> elements,
  ) => project.copyWith(
    pages: [
      for (final (i, current) in project.pages.indexed)
        i == page ? LayoutPage(elements) : current,
    ],
  );
}
