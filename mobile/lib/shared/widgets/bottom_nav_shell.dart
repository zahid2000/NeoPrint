import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/features/readiness.dart';
import '../../core/router/back_stack.dart';
import '../../features/notices/hub_notices.dart';
import '../../features/notifications/notification_host.dart';
import '../../features/schedule/schedule_due_prompt.dart';
import '../../features/session/session_banner.dart';
import '../../l10n/app_localizations.dart';

/// The bottom navigation: Ana, Fayllar, Çap, Balans, Profil (T-338;
/// docs/DECISIONS.md 2026-11-08 - the plan's section 8.1 had Kiosklar and
/// Planlanmışlar, which now live in the Print tab).
///
/// A stateful shell, so each tab keeps its scroll position and its loaded data
/// when the user moves between them.
class BottomNavShell extends ConsumerWidget {
  const BottomNavShell({required this.shell, super.key});

  final StatefulNavigationShell shell;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
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
        body: HubNotices(
          child: NotificationHost(
            child: Readiness.scheduledPrint
                ? ScheduleDuePrompt(child: shell)
                : shell,
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

  @override
  State<NavBarSwipe> createState() => _NavBarSwipeState();
}

class _NavBarSwipeState extends State<NavBarSwipe> {
  double _dx = 0;

  @override
  Widget build(BuildContext context) => GestureDetector(
    behavior: HitTestBehavior.translucent,
    onHorizontalDragStart: (_) => _dx = 0,
    onHorizontalDragUpdate: (details) => _dx += details.primaryDelta ?? 0,
    onHorizontalDragEnd: (details) {
      final speed = details.primaryVelocity ?? 0;
      final towards = speed.abs() >= NavBarSwipe.velocity ? speed : _dx;
      if (towards.abs() < NavBarSwipe.distance &&
          speed.abs() < NavBarSwipe.velocity) {
        return;
      }
      final rtl = Directionality.of(context) == TextDirection.rtl;
      final next = widget.index + ((towards < 0) != rtl ? 1 : -1);
      if (next >= 0 && next < widget.count) {
        widget.onSelected(next);
      }
    },
    child: widget.child,
  );
}
