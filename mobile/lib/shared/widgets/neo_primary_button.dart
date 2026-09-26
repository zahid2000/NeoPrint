import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';

/// The one primary action on a screen.
///
/// While it is working the button is disabled and shows a spinner, and the
/// label stays where it was: a button whose text changes to "Loading…" moves
/// everything around it and gives no clue what it was
/// (docs/DESIGN_SYSTEM.md section 4).
class NeoPrimaryButton extends StatelessWidget {
  const NeoPrimaryButton({
    super.key,
    required this.label,
    required this.onPressed,
    this.busy = false,
    this.icon,
  });

  final String label;

  /// Null disables the button, as it does on any Material button.
  final VoidCallback? onPressed;

  final bool busy;
  final IconData? icon;

  @override
  Widget build(BuildContext context) {
    final child = busy
        ? _Spinner(colour: Theme.of(context).colorScheme.onPrimary)
        : _Label(label: label, icon: icon);

    return Semantics(
      button: true,
      enabled: !busy && onPressed != null,
      label: label,
      child: ExcludeSemantics(
        child: FilledButton(onPressed: busy ? null : onPressed, child: child),
      ),
    );
  }
}

class _Label extends StatelessWidget {
  const _Label({required this.label, this.icon});

  final String label;
  final IconData? icon;

  @override
  Widget build(BuildContext context) {
    if (icon == null) {
      return Text(label);
    }

    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Icon(icon, size: 20),
        const SizedBox(width: Spacing.x2),
        Text(label),
      ],
    );
  }
}

class _Spinner extends StatelessWidget {
  const _Spinner({required this.colour});

  final Color colour;

  @override
  Widget build(BuildContext context) {
    return SizedBox.square(
      dimension: 20,
      child: CircularProgressIndicator(strokeWidth: 2, color: colour),
    );
  }
}
