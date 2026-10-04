import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/l10n/language_controller.dart';
import '../../core/network/api_client.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';

/// A language's own name: never translated, so it can be found in any
/// language the screen happens to be in.
String languageName(AppLocalizations l10n, String code) => switch (code) {
  'en' => l10n.languageNameEn,
  'ru' => l10n.languageNameRu,
  _ => l10n.languageNameAz,
};

/// What the profile row says under "Dil".
String languageSummary(AppLocalizations l10n, AppLanguage language) =>
    language.followsPhone
    ? l10n.languagePhoneValue(languageName(l10n, language.phone))
    : languageName(l10n, language.code);

/// Profile → "Dil" (docs/DESIGN_SYSTEM.md "Mobil: dil və hüquqi sənədlər").
Future<void> showLanguageSheet(BuildContext context) => showModalBottomSheet(
  context: context,
  showDragHandle: true,
  isScrollControlled: true,
  builder: (_) => const LanguageSheet(),
);

class LanguageSheet extends ConsumerWidget {
  const LanguageSheet({super.key});

  static const _choices = <String?>[null, 'az', 'en', 'ru'];

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final text = Theme.of(context).textTheme;
    final language = ref.watch(appLanguageProvider);

    Future<void> choose(String? code) async {
      final messenger = ScaffoldMessenger.of(context);
      final navigator = Navigator.of(context);
      navigator.pop();

      if (code == language.choice) {
        return;
      }

      try {
        await ref.read(appLanguageProvider.notifier).choose(code);
      } on ApiException catch (error) {
        messenger.showSnackBar(
          SnackBar(
            content: Text(l10n.languageSaveFailed(apiErrorText(l10n, error))),
          ),
        );
      }
    }

    return SafeArea(
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(
          Spacing.x4,
          0,
          Spacing.x4,
          Spacing.x4,
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(l10n.languageTitle, style: text.titleLarge),
            const SizedBox(height: Spacing.x2),
            Text(l10n.languageHint, style: text.bodyMedium),
            const SizedBox(height: Spacing.x2),
            RadioGroup<String?>(
              groupValue: language.choice,
              onChanged: choose,
              child: Column(
                children: [
                  for (final code in _choices)
                    RadioListTile<String?>(
                      value: code,
                      contentPadding: EdgeInsets.zero,
                      title: Text(
                        code == null
                            ? l10n.languagePhone
                            : languageName(l10n, code),
                      ),
                      subtitle: code == null
                          ? Text(languageName(l10n, language.phone))
                          : null,
                    ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}
