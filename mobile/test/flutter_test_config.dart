import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/time/app_clock.dart';

/// The narrowest phone the app is laid out for, in logical pixels
/// (docs/DESIGN_SYSTEM.md section 5.1).
const smallPhone = Size(360, 800);

/// The day every test in this folder runs on, as far as "today" and
/// "yesterday" on screen are concerned (docs/TASKS.md T-246).
///
/// Without it a date written into a test was read against the real clock: a
/// test showing 14.03.2030 would fail on that day and the next, when the
/// screen says "Bu gün" instead, and one expecting "Bu gün" for a file made a
/// moment ago could fail at midnight. No date written into a test is this
/// day or the day before it.
final testNow = DateTime(2026, 11, 15, 12);

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
    setAppClock(() => testNow);
    addTearDown(() => setAppClock(null));

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
