import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/features/readiness.dart';
import '../../core/router/back_stack.dart';
import '../../core/theme/tokens.dart';
import '../../features/notices/hub_notices.dart';
import '../../features/notifications/notification_host.dart';
import '../../features/schedule/schedule_due_prompt.dart';
import '../../features/session/session_banner.dart';
import '../../l10n/app_localizations.dart';
import 'neo_design.dart';

/// The bottom navigation: Ana, Fayllar, Çap, Balans, Profil (T-338;
/// docs/DECISIONS.md 2026-11-08 - the plan's section 8.1 had Kiosklar and
/// Planlanmışlar, which now live in the Print tab).
///
/// A stateful shell, so each tab keeps its scroll position and its loaded data
/// when the user moves between them.
///
/// Moving between them reads as moving sideways (T-432): the next tab's
/// screen slides in from the side it lies on, as a tabbed page's list does
/// (docs/DESIGN_SYSTEM.md "Hap tabları"), and a swipe along the bar takes the
/// screen with the finger.
class BottomNavShell extends ConsumerStatefulWidget {
  const BottomNavShell({required this.shell, super.key});

  final StatefulNavigationShell shell;

  @override
  ConsumerState<BottomNavShell> createState() => _BottomNavShellState();
}

class _BottomNavShellState extends ConsumerState<BottomNavShell>
    with SingleTickerProviderStateMixin {
  /// The screen's sideways offset, in logical pixels.
  late final _offset = AnimationController.unbounded(vsync: this);
  late int _index = widget.shell.currentIndex;

  StatefulNavigationShell get shell => widget.shell;

  bool get _rtl => Directionality.of(context) == TextDirection.rtl;

  double get _width => MediaQuery.sizeOf(context).width;

  @override
  void didUpdateWidget(BottomNavShell old) {
    super.didUpdateWidget(old);
    final now = widget.shell.currentIndex;
    if (now != _index) {
      // From a tap, a swipe, a button or back: in from the side it lies on.
      final forward = now > _index;
      _index = now;
      final side = forward != _rtl ? 1.0 : -1.0;
      _offset.value = side * _width * NeoTabSwipe.entryShare;
      unawaited(
        _offset.animateTo(0, duration: Motion.base, curve: Motion.easing),
      );
    }
  }

  @override
  void dispose() {
    _offset.dispose();
    super.dispose();
  }

  /// The bar dragged [dx] so far: the screen goes with it, a little only
  /// where there is no tab beyond.
  void _dragged(double dx, int count) {
    final forward = (dx < 0) != _rtl;
    final next = _index + (forward ? 1 : -1);
    _offset.value = next >= 0 && next < count
        ? dx
        : dx * NeoTabSwipe.edgeResistance;
  }

  void _settle() => unawaited(
    _offset.animateTo(0, duration: Motion.fast, curve: Motion.easing),
  );

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    // The same order as the branches in app_router.dart.
    const destinations = [
      (Icons.home_outlined, Icons.home),
      (Icons.folder_outlined, Icons.folder),
      (Icons.print_outlined, Icons.print),
      (Icons.account_balance_wallet_outlined, Icons.account_balance_wallet),
      (Icons.person_outline, Icons.person),
    ];
    final labels = [
      l10n.navTabHome,
      l10n.navTabFiles,
      l10n.navPrint,
      l10n.navWallet,
      l10n.navProfile,
    ];

    // Back on a tab's first screen (T-348): to the screen a button came from,
    // else to Home, and only from Home out of the app. Inside a tab the
    // tab's own navigator pops first and this is not asked.
    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (didPop, _) {
        if (didPop) {
          return;
        }
        if (ref.read(backStackProvider).back(GoRouter.of(context))) {
          return;
        }
        if (shell.currentIndex != 0) {
          shell.goBranch(0);
          return;
        }
        SystemNavigator.pop();
      },
      child: Scaffold(
        // Inside the scaffold, so a notice sits above the navigation bar.
        body: ClipRect(
          child: AnimatedBuilder(
            animation: _offset,
            builder: (context, child) {
              final share = (_offset.value.abs() / _width).clamp(0.0, 1.0);
              return Opacity(
                // As a tab's list does: lighter as it goes, never blank.
                opacity: (1 - share * 0.8).clamp(0.3, 1.0),
                child: Transform.translate(
                  offset: Offset(_offset.value, 0),
                  child: child,
                ),
              );
            },
            child: HubNotices(
              child: NotificationHost(
                child: Readiness.scheduledPrint
                    ? ScheduleDuePrompt(child: shell)
                    : shell,
              ),
            ),
          ),
        ),
        // The open session sits just above the bar, on every tab.
        bottomNavigationBar: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (Readiness.mobileSession) const SessionBanner(),
            // A swipe along the bar steps to the next or the previous tab
            // (T-431), as a tap on it would.
            NavBarSwipe(
              index: shell.currentIndex,
              count: destinations.length,
              onDrag: (dx) => _dragged(dx, destinations.length),
              onRelease: _settle,
              onSelected: (index) {
                ref.read(backStackProvider).clear();
                shell.goBranch(index);
              },
              child: NavigationBar(
                selectedIndex: shell.currentIndex,
                // goBranch with initialLocation restores a tab to its root when it is
                // tapped again, which is what people expect from a tab bar.
                onDestinationSelected: (index) {
                  // The tab bar is a move sideways: nothing to go back to.
                  ref.read(backStackProvider).clear();
                  shell.goBranch(
                    index,
                    initialLocation: index == shell.currentIndex,
                  );
                },
                destinations: [
                  for (var i = 0; i < destinations.length; i++)
                    NavigationDestination(
                      icon: Icon(destinations[i].$1),
                      selectedIcon: Icon(destinations[i].$2),
                      label: labels[i],
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

/// The bottom bar swiped sideways (T-431): right to left is the next tab,
/// left to right the previous one; a short or slow move does nothing.
class NavBarSwipe extends StatefulWidget {
  const NavBarSwipe({
    required this.index,
    required this.count,
    required this.onSelected,
    required this.child,
    this.onDrag,
    this.onRelease,
    super.key,
  });

  /// Logical pixels; a drag shorter than this is not a swipe.
  static const distance = 48.0;

  /// Logical pixels a second; a quicker flick is a swipe however short.
  static const velocity = 300.0;

  final int index;
  final int count;
  final ValueChanged<int> onSelected;
  final Widget child;

  /// How far the finger has gone along the bar so far, for the screen to
  /// follow it (T-432).
  final ValueChanged<double>? onDrag;

  /// Let go without a step to another tab: the screen goes back.
  final VoidCallback? onRelease;

  @override
  State<NavBarSwipe> createState() => _NavBarSwipeState();
}

class _NavBarSwipeState extends State<NavBarSwipe> {
  double _dx = 0;

  @override
  Widget build(BuildContext context) => GestureDetector(
    behavior: HitTestBehavior.translucent,
    onHorizontalDragStart: (_) => _dx = 0,
    onHorizontalDragUpdate: (details) {
      _dx += details.primaryDelta ?? 0;
      widget.onDrag?.call(_dx);
    },
    onHorizontalDragCancel: () => widget.onRelease?.call(),
    onHorizontalDragEnd: (details) {
      final speed = details.primaryVelocity ?? 0;
      final towards = speed.abs() >= NavBarSwipe.velocity ? speed : _dx;
      // A fling the other way from where the screen was taken: it goes back.
      final turned = _dx != 0 && towards.sign != _dx.sign;
      if (turned ||
          (towards.abs() < NavBarSwipe.distance &&
              speed.abs() < NavBarSwipe.velocity)) {
        widget.onRelease?.call();
        return;
      }
      final rtl = Directionality.of(context) == TextDirection.rtl;
      final next = widget.index + ((towards < 0) != rtl ? 1 : -1);
      if (next >= 0 && next < widget.count) {
        widget.onSelected(next);
      } else {
        widget.onRelease?.call();
      }
    },
    child: widget.child,
  );
}
