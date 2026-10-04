import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import 'file_models.dart';
import 'files_controller.dart';

/// "Avtomatik silinmə" for one file (docs/DESIGN_SYSTEM.md, "Mobil: məxfilik"):
/// a choice saves at once and closes the sheet.
Future<void> showAutoDeleteSheet(BuildContext context, UserFile file) =>
    showModalBottomSheet<void>(
      context: context,
      showDragHandle: true,
      // Five choices and their words do not fit a phone held sideways.
      isScrollControlled: true,
      builder: (_) => _AutoDeleteSheet(file: file),
    );

/// What the file card says about a rule, or null for "never".
String? autoDeleteChipLabel(AppLocalizations l10n, AutoDeleteRule rule) =>
    switch (rule) {
      AutoDeleteRule.never => null,
      AutoDeleteRule.afterPrint => l10n.filesAutoDeleteChipAfterPrint,
      _ => l10n.filesAutoDeleteChipDays(rule.days!),
    };

class _AutoDeleteSheet extends ConsumerStatefulWidget {
  const _AutoDeleteSheet({required this.file});

  final UserFile file;

  @override
  ConsumerState<_AutoDeleteSheet> createState() => _AutoDeleteSheetState();
}

class _AutoDeleteSheetState extends ConsumerState<_AutoDeleteSheet> {
  bool _busy = false;

  Future<void> _choose(AutoDeleteRule? rule) async {
    if (rule == null || rule == widget.file.autoDelete || _busy) {
      Navigator.pop(context);
      return;
    }

    setState(() => _busy = true);
    final l10n = AppLocalizations.of(context);
    final messenger = ScaffoldMessenger.of(context);
    final problem = await ref
        .read(filesControllerProvider.notifier)
        .setAutoDelete(widget.file, rule);

    if (!mounted) {
      return;
    }

    Navigator.pop(context);
    if (problem != null) {
      messenger.showSnackBar(
        SnackBar(content: Text(apiErrorText(l10n, problem))),
      );
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;

    String label(AutoDeleteRule rule) => switch (rule) {
      AutoDeleteRule.never => l10n.filesAutoDeleteNever,
      AutoDeleteRule.afterPrint => l10n.filesAutoDeleteAfterPrint,
      _ => l10n.filesAutoDeleteAfterDays(rule.days!),
    };

    return SafeArea(
      child: SingleChildScrollView(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(
            Spacing.x4,
            0,
            Spacing.x4,
            Spacing.x4,
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(l10n.filesAutoDeleteMenu, style: theme.textTheme.titleLarge),
              const SizedBox(height: Spacing.x2),
              Text(
                l10n.filesAutoDeleteExplanation,
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: tokens.neutral600,
                ),
              ),
              const SizedBox(height: Spacing.x2),
              RadioGroup<AutoDeleteRule>(
                groupValue: widget.file.autoDelete,
                onChanged: _busy ? (_) {} : _choose,
                child: Column(
                  children: [
                    for (final rule in AutoDeleteRule.values)
                      RadioListTile<AutoDeleteRule>(
                        value: rule,
                        contentPadding: EdgeInsets.zero,
                        title: Text(label(rule)),
                        subtitle: rule == AutoDeleteRule.afterPrint
                            ? Text(l10n.filesAutoDeleteAfterPrintHint)
                            : null,
                      ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
