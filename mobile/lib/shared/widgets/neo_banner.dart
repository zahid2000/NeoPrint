import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';

/// A message above a form or a list: an error, a warning or a note.
///
/// Colour plus icon plus text, never colour alone
/// (docs/DESIGN_SYSTEM.md principle 6). An error says what happened; what it
/// affected and what to do next belong in the message the caller passes.
class NeoBanner extends StatelessWidget {
  const NeoBanner({
    super.key,
    required this.message,
    this.tone = StatusTone.danger,
    this.icon,
    this.onClose,
  });

  /// Shorthand for the common case.
  const NeoBanner.error({super.key, required this.message, this.onClose})
    : tone = StatusTone.danger,
      icon = null;

  final String message;
  final StatusTone tone;

  /// Overrides the icon the tone would pick.
  final IconData? icon;

  /// Shows a close button, for a message that stays until it is dismissed.
  final VoidCallback? onClose;

  static const _icons = <StatusTone, IconData>{
    StatusTone.success: Icons.check_circle_outline,
    StatusTone.warning: Icons.warning_amber_outlined,
    StatusTone.danger: Icons.error_outline,
    StatusTone.info: Icons.info_outline,
    StatusTone.neutral: Icons.info_outline,
  };

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;
    final colours = tokens.forTone(tone);

    return Semantics(
      liveRegion: true,
      child: Container(
        padding: const EdgeInsets.all(Spacing.x3),
        decoration: BoxDecoration(
          color: colours.background,
          borderRadius: BorderRadius.circular(Radii.md),
        ),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Icon(icon ?? _icons[tone], color: colours.foreground, size: 20),
            const SizedBox(width: Spacing.x3),
            Expanded(
              child: Text(
                message,
                style: Theme.of(context).textTheme.bodyMedium
                    ?.copyWith(color: colours.foreground),
              ),
            ),
            if (onClose != null)
              IconButton(
                onPressed: onClose,
                tooltip: MaterialLocalizations.of(context).closeButtonTooltip,
                icon: Icon(Icons.close, color: colours.foreground),
              ),
          ],
        ),
      ),
    );
  }
}
