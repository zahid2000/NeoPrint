import 'package:flutter/material.dart';

/// Keeps every screen above the phone's own navigation bar (T-352).
///
/// Android 15 draws apps edge to edge whatever the app asks: a list with its
/// own padding scrolled its last lines under the system buttons. Taking the
/// bottom inset off once, around the whole app, covers every screen, dialog
/// and bottom sheet; the strip below is painted the colour of the app's own
/// bottom bar so the two read as one.
class SystemBarFrame extends StatelessWidget {
  const SystemBarFrame({super.key, required this.child});

  final Widget? child;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return ColoredBox(
      color:
          theme.navigationBarTheme.backgroundColor ?? theme.colorScheme.surface,
      child: SafeArea(
        top: false,
        left: false,
        right: false,
        child: child ?? const SizedBox.shrink(),
      ),
    );
  }
}
