import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';

/// The colour family of a [NeoIconTile]. Decoration only: what a row means is
/// in its text (docs/DESIGN_SYSTEM.md principle 6).
enum IconTileTone { brand, success, warning, info, danger }

/// A coloured square with an icon: the start of a list row and of a home
/// shortcut (docs/DESIGN_SYSTEM.md, "Mobil: yeni vizual dil").
class NeoIconTile extends StatelessWidget {
  const NeoIconTile({
    required this.icon,
    this.tone = IconTileTone.brand,
    super.key,
  });

  final IconData icon;
  final IconTileTone tone;

  static const double size = 40;

  @override
  Widget build(BuildContext context) {
    final colours = iconTileColours(context, tone);

    return Container(
      width: size,
      height: size,
      decoration: BoxDecoration(
        color: colours.background,
        borderRadius: BorderRadius.circular(Radii.sm),
      ),
      child: Icon(icon, size: 24, color: colours.foreground),
    );
  }
}

({Color foreground, Color background}) iconTileColours(
  BuildContext context,
  IconTileTone tone,
) {
  final tokens = context.tokens;

  return switch (tone) {
    IconTileTone.brand => (
      foreground: tokens.brand500,
      background: tokens.brand50,
    ),
    IconTileTone.success => tokens.forTone(StatusTone.success),
    IconTileTone.warning => tokens.forTone(StatusTone.warning),
    IconTileTone.info => tokens.forTone(StatusTone.info),
    IconTileTone.danger => tokens.forTone(StatusTone.danger),
  };
}

/// A card row: icon square, title, one line of hint, and a chevron - the
/// whole card is the button.
class NeoNavRow extends StatelessWidget {
  const NeoNavRow({
    required this.icon,
    required this.title,
    required this.onTap,
    this.hint,
    this.tone = IconTileTone.brand,
    this.trailing,
    super.key,
  });

  final IconData icon;
  final String title;
  final String? hint;
  final IconTileTone tone;
  final VoidCallback onTap;

  /// Before the chevron - a count badge, say.
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Card(
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.all(Spacing.x3),
          child: Row(
            children: [
              NeoIconTile(icon: icon, tone: tone),
              const SizedBox(width: Spacing.x3),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(title, style: theme.textTheme.titleSmall),
                    if (hint != null)
                      Text(
                        hint!,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: context.tokens.neutral600,
                        ),
                      ),
                  ],
                ),
              ),
              if (trailing != null) ...[
                const SizedBox(width: Spacing.x2),
                trailing!,
              ],
              Icon(Icons.chevron_right, color: context.tokens.neutral400),
            ],
          ),
        ),
      ),
    );
  }
}
