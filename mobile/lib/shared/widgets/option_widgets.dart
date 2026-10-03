import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';

// The print options' building blocks, shared by "Kiosk üçün hazırla" and
// "Çapı planla" (docs/DESIGN_SYSTEM.md section 4, "Mobil").

/// A choice whose first segment is "at the kiosk" while no kiosk is chosen.
class OptionChoice<T extends Object> extends StatelessWidget {
  const OptionChoice({
    super.key,
    required this.atKiosk,
    required this.atKioskLabel,
    required this.value,
    required this.choices,
    required this.onChanged,
  });

  final bool atKiosk;
  final String atKioskLabel;
  final T? value;

  /// (value, label, whether this kiosk can do it).
  final List<(T, String, bool)> choices;
  final ValueChanged<T?> onChanged;

  @override
  Widget build(BuildContext context) => SegmentedButton<T?>(
    segments: [
      if (atKiosk) ButtonSegment<T?>(value: null, label: Text(atKioskLabel)),
      for (final (choice, label, enabled) in choices)
        ButtonSegment<T?>(value: choice, label: Text(label), enabled: enabled),
    ],
    selected: {value},
    onSelectionChanged: (selected) => onChanged(selected.single),
  );
}

/// The title above one option.
class OptionLabel extends StatelessWidget {
  const OptionLabel(this.text, {super.key});

  final String text;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: Spacing.x2),
    child: Text(text, style: Theme.of(context).textTheme.titleSmall),
  );
}

/// Why a choice is not there: the kiosk's real limit.
class OptionWhy extends StatelessWidget {
  const OptionWhy(this.text, {super.key});

  final String text;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(top: Spacing.x1),
    child: Text(
      text,
      style: Theme.of(context).textTheme.bodySmall
          ?.copyWith(color: context.tokens.neutral600),
    ),
  );
}
