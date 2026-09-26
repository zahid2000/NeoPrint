import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';

/// What a list shows when it has nothing in it.
///
/// An icon, one sentence saying why it is empty, and where there is one, the
/// action that would fill it. "No data" on its own is not an empty state
/// (docs/DESIGN_SYSTEM.md section 4).
class NeoEmptyState extends StatelessWidget {
  const NeoEmptyState({
    super.key,
    required this.icon,
    required this.message,
    this.hint,
    this.action,
  });

  final IconData icon;

  /// One sentence: why is this empty?
  final String message;

  /// Optional second line: what would put something here.
  final String? hint;

  final Widget? action;

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;
    final text = Theme.of(context).textTheme;

    return Center(
      child: Padding(
        padding: const EdgeInsets.symmetric(
          horizontal: Spacing.x6,
          vertical: Spacing.x12,
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 48, color: tokens.neutral400),
            const SizedBox(height: Spacing.x4),
            Text(message, textAlign: TextAlign.center, style: text.titleMedium),
            if (hint != null) ...[
              const SizedBox(height: Spacing.x2),
              Text(hint!, textAlign: TextAlign.center, style: text.bodySmall),
            ],
            if (action != null) ...[
              const SizedBox(height: Spacing.x6),
              action!,
            ],
          ],
        ),
      ),
    );
  }
}
