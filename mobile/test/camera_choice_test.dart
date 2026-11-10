import 'package:camera/camera.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/platform/camera_choice.dart';

CameraDescription cam(String name, CameraLensDirection direction) =>
    CameraDescription(
      name: name,
      lensDirection: direction,
      sensorOrientation: 90,
    );

void main() {
  test('the main back camera comes first, whatever order the phone lists '
      'them in (T-346)', () {
    final cameras = [
      cam('2', CameraLensDirection.back), // ultra-wide
      cam('1', CameraLensDirection.front),
      cam('3', CameraLensDirection.back), // macro
      cam('0', CameraLensDirection.back), // main
    ];

    expect(backCamerasMainFirst(cameras).map((c) => c.name), ['0', '2', '3']);
  });

  test('names that are not numbers keep their order after the numbered '
      'ones', () {
    final cameras = [
      cam('wide', CameraLensDirection.back),
      cam('tele', CameraLensDirection.back),
      cam('5', CameraLensDirection.back),
    ];

    expect(backCamerasMainFirst(cameras).map((c) => c.name), [
      '5',
      'wide',
      'tele',
    ]);
  });

  test('a phone with only a front camera has no back one', () {
    expect(
      backCamerasMainFirst([cam('1', CameraLensDirection.front)]),
      isEmpty,
    );
  });

  test('the scanner steps from the main back camera to the front one, then '
      'the other lenses (T-406)', () {
    final cameras = [
      cam('2', CameraLensDirection.back),
      cam('1', CameraLensDirection.front),
      cam('0', CameraLensDirection.back),
    ];

    expect(scanCamerasInOrder(cameras).map((c) => c.name), ['0', '1', '2']);
    expect(
      scanCamerasInOrder([cam('1', CameraLensDirection.front)])
          .map((c) => c.name),
      ['1'],
    );
  });
}
