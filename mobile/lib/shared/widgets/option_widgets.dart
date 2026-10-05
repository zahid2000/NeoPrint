import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';
import 'neo_banner.dart';

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
    this.locked = false,
  });

  final bool atKiosk;
  final String atKioskLabel;
  final T? value;

  /// (value, label, whether this kiosk can do it).
  final List<(T, String, bool)> choices;
  final ValueChanged<T?> onChanged;

  /// Fixed for this file: the value stays shown as chosen, nothing else can
  /// be picked (docs/DESIGN_SYSTEM.md, "Real ölçü kilidi").
  final bool locked;

  @override
  Widget build(BuildContext context) => SegmentedButton<T?>(
    segments: [
      if (atKiosk) ButtonSegment<T?>(value: null, label: Text(atKioskLabel)),
      for (final (choice, label, enabled) in choices)
        ButtonSegment<T?>(value: choice, label: Text(label), enabled: enabled),
    ],
    selected: {value},
    onSelectionChanged: locked
        ? null
        : (selected) => onChanged(selected.single),
  );
}

/// Why paper and pages per sheet are fixed: a scan or layout prints at real
/// size (plan section 2.11; docs/DESIGN_SYSTEM.md, "Real ölçü kilidi").
class RealSizeNote extends StatelessWidget {
  const RealSizeNote(this.text, {super.key, this.blocked = false});

  final String text;

  /// The kiosk cannot print it at all (an A3 layout without A3).
  final bool blocked;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: Spacing.x4),
    child: NeoBanner(
      tone: blocked ? StatusTone.warning : StatusTone.info,
      message: text,
    ),
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
