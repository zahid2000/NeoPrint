import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

/// The narrowest phone the app is laid out for, in logical pixels
/// (docs/DESIGN_SYSTEM.md section 5.1).
const smallPhone = Size(360, 800);

/// Every widget test in this folder is drawn on a small phone.
///
/// flutter_test's own screen is 800×600 - a tablet on its side. A row that
/// fits there and does not fit on a phone passed every test and overflowed in
/// somebody's hand. With this, a screen that overflows at 360 dp fails the
/// test that draws it: Flutter reports an overflow as an error, and the test
/// framework turns that into a failure by itself.
///
/// A test that needs another size - a tall one so a long list is all on
/// screen - sets it as before; that wins over this default.
Future<void> testExecutable(FutureOr<void> Function() testMain) async {
  setUp(() {
    final binding = TestWidgetsFlutterBinding.ensureInitialized();
    final view = binding.platformDispatcher.implicitView;

    if (view == null) {
      return;
    }

    view.physicalSize = smallPhone;
    view.devicePixelRatio = 1;
    addTearDown(view.reset);
  });

  await testMain();
}
