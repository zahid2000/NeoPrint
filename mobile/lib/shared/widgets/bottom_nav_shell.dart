import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../core/features/readiness.dart';
import '../../features/notices/hub_notices.dart';
import '../../features/notifications/notification_host.dart';
import '../../features/schedule/schedule_due_prompt.dart';
import '../../features/session/session_banner.dart';
import '../../l10n/app_localizations.dart';

/// The bottom navigation from plan section 8.1: five destinations once every
/// one of them has its screen, fewer until then (Readiness).
///
/// A stateful shell, so each tab keeps its scroll position and its loaded data
/// when the user moves between them.
class BottomNavShell extends StatelessWidget {
  const BottomNavShell({required this.shell, super.key});

  final StatefulNavigationShell shell;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

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
            selectedIndex: shell.currentIndex,
            // goBranch with initialLocation restores a tab to its root when it is
            // tapped again, which is what people expect from a tab bar.
            onDestinationSelected: (index) => shell.goBranch(
              index,
              initialLocation: index == shell.currentIndex,
            ),
            destinations: [
              NavigationDestination(
                icon: const Icon(Icons.home_outlined),
                selectedIcon: const Icon(Icons.home),
                label: l10n.navHome,
              ),
              // The same conditions as the branches in app_router.dart: the bar
              // and the shell must list the same tabs in the same order.
              if (Readiness.kioskSearch)
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
              if (Readiness.scheduledPrint)
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
            ],
          ),
        ],
      ),
    );
  }
}
