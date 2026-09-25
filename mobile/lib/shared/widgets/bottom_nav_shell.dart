import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../l10n/app_localizations.dart';

/// The five-destination bottom navigation from plan section 8.1.
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
      body: shell,
      bottomNavigationBar: NavigationBar(
        selectedIndex: shell.currentIndex,
        // goBranch with initialLocation restores a tab to its root when it is
        // tapped again, which is what people expect from a tab bar.
        onDestinationSelected: (index) =>
            shell.goBranch(index, initialLocation: index == shell.currentIndex),
        destinations: [
          NavigationDestination(
            icon: const Icon(Icons.home_outlined),
            selectedIcon: const Icon(Icons.home),
            label: l10n.navHome,
          ),
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
    );
  }
}
