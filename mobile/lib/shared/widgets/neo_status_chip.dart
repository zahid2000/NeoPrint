import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';

/// A status, shown as colour plus icon plus text.
///
/// All three, always: colour alone carries no meaning for somebody who cannot
/// tell these colours apart (docs/DESIGN_SYSTEM.md principle 6).
class NeoStatusChip extends StatelessWidget {
  const NeoStatusChip({
    super.key,
    required this.label,
    required this.tone,
    this.icon,
  });

  final String label;
  final StatusTone tone;

  /// Overrides the icon the tone would pick.
  final IconData? icon;

  static const _icons = <StatusTone, IconData>{
    StatusTone.success: Icons.check_circle_outline,
    StatusTone.warning: Icons.warning_amber_outlined,
    StatusTone.danger: Icons.error_outline,
    StatusTone.info: Icons.sync,
    StatusTone.neutral: Icons.radio_button_unchecked,
  };

  @override
  Widget build(BuildContext context) {
    final colours = context.tokens.forTone(tone);

    return Container(
      height: Sizes.statusChipHeight,
      padding: const EdgeInsets.symmetric(horizontal: Spacing.x2),
      decoration: BoxDecoration(
        color: colours.background,
        borderRadius: BorderRadius.circular(Radii.full),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon ?? _icons[tone], size: 14, color: colours.foreground),
          const SizedBox(width: Spacing.x1),
          Text(
            label,
            style: TextStyle(
              color: colours.foreground,
              fontSize: FontSizes.caption,
              fontWeight: FontWeights.medium,
            ),
          ),
        ],
      ),
    );
  }
}
