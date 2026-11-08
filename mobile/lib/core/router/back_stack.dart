import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

/// Where the user was before a button switched tabs (T-348).
///
/// A tab is switched to with `go`, which replaces the whole route stack: back
/// then had nothing to return to and closed the app - "Fayllarımdan seç" in
/// the Print tab, a notification that opens Balance, the home screen's
/// cards. Before such a switch the screen the user was on is remembered
/// here, and back on a tab's first screen returns to it.
///
/// Tapping the tab bar is a move sideways, not a step forward: it clears
/// this, and back on a tab other than Home then goes Home.
class BackStack {
  /// Enough for any real path through the app; the oldest go first.
  static const limit = 20;

  final _entries = <RouteMatchList>[];

  bool get isEmpty => _entries.isEmpty;

  /// Switches to [location], remembering where the user is now.
  void go(GoRouter router, String location, {Object? extra}) {
    _entries.add(router.routerDelegate.currentConfiguration);
    if (_entries.length > limit) {
      _entries.removeAt(0);
    }
    router.go(location, extra: extra);
  }

  /// Returns to the last remembered screen; false when there is none.
  bool back(GoRouter router) {
    if (_entries.isEmpty) {
      return false;
    }
    router.restore(_entries.removeLast());
    return true;
  }

  void clear() => _entries.clear();
}

final backStackProvider = Provider<BackStack>((_) => BackStack());

extension BackStackNavigation on BuildContext {
  /// `go` to a tab (or a screen inside one) so that back comes here again.
  void goTab(String location, {Object? extra}) => ProviderScope.containerOf(
    this,
    listen: false,
  ).read(backStackProvider).go(GoRouter.of(this), location, extra: extra);
}
