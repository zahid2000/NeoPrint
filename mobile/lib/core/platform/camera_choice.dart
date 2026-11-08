import 'package:camera/camera.dart';

/// The back cameras, the phone's main one first (T-346).
///
/// A phone with several lenses lists them all: on Android the ultra-wide,
/// macro or depth camera can come before the main one, and taking the first
/// back camera gave a blurred, fish-eye or unfocused picture. The main back
/// camera has the lowest id ("0" on Android; on iOS the built-in wide one is
/// listed first), so the back cameras are sorted by id and the first is the
/// main one; the rest are what "another camera" steps through.
List<CameraDescription> backCamerasMainFirst(List<CameraDescription> all) {
  final back = [
    for (final camera in all)
      if (camera.lensDirection == CameraLensDirection.back) camera,
  ];

  int? id(CameraDescription camera) => int.tryParse(camera.name);

  // A stable sort by numeric id; names that are not numbers keep their place
  // after the numbered ones.
  final indexed = [for (var i = 0; i < back.length; i++) (i, back[i])];
  indexed.sort((a, b) {
    final ia = id(a.$2);
    final ib = id(b.$2);
    if (ia != null && ib != null) {
      return ia.compareTo(ib);
    }
    if (ia != null) {
      return -1;
    }
    if (ib != null) {
      return 1;
    }
    return a.$1.compareTo(b.$1);
  });

  return [for (final entry in indexed) entry.$2];
}
