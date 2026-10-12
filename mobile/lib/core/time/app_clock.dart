/// What time it is, for everything the app writes as a calendar day: "today",
/// "yesterday", a date.
///
/// The phone's clock - except in a test, which sets its own. A test that shows
/// a date written into it against the real clock passes on the day it is
/// written and fails on another: the date becomes "today", or stops being it
/// (docs/TASKS.md T-246).
library;

import 'package:flutter/foundation.dart';

DateTime Function() _now = DateTime.now;

/// The current moment.
DateTime appNow() => _now();

/// Replaces the clock for a test; `null` gives the phone's clock back.
@visibleForTesting
void setAppClock(DateTime Function()? now) => _now = now ?? DateTime.now;
