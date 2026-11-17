import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'core/l10n/app_delegates.dart';
import 'core/l10n/app_language.dart';
import 'core/l10n/language_controller.dart';
import 'core/router/app_router.dart';
import 'core/theme/app_theme.dart';
import 'l10n/app_localizations.dart';
import 'shared/widgets/system_bar_frame.dart';

void main() {
  runApp(const ProviderScope(child: NeoPrintApp()));
}

/// The NeoPrint mobile application (plan section 8).
class NeoPrintApp extends ConsumerWidget {
  const NeoPrintApp({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final router = ref.watch(routerProvider);
    final language = ref.watch(appLanguageProvider);

    return MaterialApp.router(
      title: 'NeoPrint',
      debugShowCheckedModeBanner: false,

      routerConfig: router,
      builder: (context, child) => SystemBarFrame(child: child),

      theme: AppTheme.light(),
      darkTheme: AppTheme.dark(),
      themeMode: ThemeMode.system,

      // Plan section 2.1: az is the default, with en and ru alongside. The
      // language chosen in the profile (T-152); otherwise the device's when
      // it is one of the three, and Azerbaijani otherwise.
      locale: language.followsPhone ? null : Locale(language.code),
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: appLocalizationsDelegates,
      localeResolutionCallback: (deviceLocale, _) =>
          Locale(resolveLanguageCode(deviceLocale)),
    );
  }
}
