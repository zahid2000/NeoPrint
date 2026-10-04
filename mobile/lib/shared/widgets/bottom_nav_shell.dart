import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/features/feature_flags.dart';
import '../../core/features/readiness.dart';
import '../../features/notices/hub_notices.dart';
import '../../features/notifications/notification_host.dart';
import '../../features/schedule/schedule_due_prompt.dart';
import '../../features/schedule/schedule_repository.dart';
import '../../features/session/session_banner.dart';
import '../../l10n/app_localizations.dart';

/// The bottom navigation from plan section 8.1: five destinations once every
/// one of them has its screen, fewer until then (Readiness).
///
/// A stateful shell, so each tab keeps its scroll position and its loaded data
/// when the user moves between them.
class BottomNavShell extends ConsumerWidget {
  const BottomNavShell({required this.shell, super.key});

  final StatefulNavigationShell shell;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);

    // The same conditions and order as the branches in app_router.dart; a
    // destination carries the index of its branch, so a tab hidden by a flag
    // does not shift the others.
    final branches = <int>[];
    var next = 0;
    int add() => next++;

    final home = add();
    final kiosks = Readiness.kioskSearch ? add() : null;
    final files = add();
    final scheduled = Readiness.scheduledPrint ? add() : null;
    final profile = add();

    // Switched off, the tab stays while a paid schedule is still waiting
    // (docs/DECISIONS.md, T-148), and while it is the one on screen.
    final showScheduled =
        scheduled != null &&
        (ref.watch(featureOnProvider(FeatureKey.scheduledPrint)) ||
            shell.currentIndex == scheduled ||
            (ref
                    .watch(schedulesProvider)
                    .valueOrNull
                    ?.any((item) => item.status.isActive) ??
                false));

    final destinations = <NavigationDestination>[
      NavigationDestination(
        icon: const Icon(Icons.home_outlined),
        selectedIcon: const Icon(Icons.home),
        label: l10n.navHome,
      ),
      if (kiosks != null)
        NavigationDestination(
          icon: const Icon(Icons.place_outlined),
          selectedIcon: const Icon(Icons.place),
          label: l10n.navKiosks,
        ),
      NavigationDestination(
        icon: const Icon(Icons.folder_outlined),
        selectedIcon: const Icon(Icons.folder),
        label: l10n.navFiles,
      ),
      if (showScheduled)
        NavigationDestination(
          icon: const Icon(Icons.schedule_outlined),
          selectedIcon: const Icon(Icons.schedule),
          label: l10n.navScheduled,
        ),
      NavigationDestination(
        icon: const Icon(Icons.person_outline),
        selectedIcon: const Icon(Icons.person),
        label: l10n.navProfile,
      ),
    ];
    branches.addAll([
      home,
      if (kiosks != null) kiosks,
      files,
      if (showScheduled) scheduled,
      profile,
    ]);

    final selected = branches.indexOf(shell.currentIndex);

    return Scaffold(
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
          NavigationBar(
            selectedIndex: selected < 0 ? 0 : selected,
            // goBranch with initialLocation restores a tab to its root when it is
            // tapped again, which is what people expect from a tab bar.
            onDestinationSelected: (index) => shell.goBranch(
              branches[index],
              initialLocation: branches[index] == shell.currentIndex,
            ),
            destinations: destinations,
          ),
        ],
      ),
    );
  }
}
