import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'core/router/app_router.dart';
import 'core/theme/app_theme.dart';
import 'l10n/app_localizations.dart';

void main() {
  runApp(const ProviderScope(child: NeoPrintApp()));
}

/// The NeoPrint mobile application (plan section 8).
class NeoPrintApp extends ConsumerWidget {
  const NeoPrintApp({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final router = ref.watch(routerProvider);

    return MaterialApp.router(
      title: 'NeoPrint',
      debugShowCheckedModeBanner: false,

      routerConfig: router,

      theme: AppTheme.light(),
      darkTheme: AppTheme.dark(),
      themeMode: ThemeMode.system,

      // Plan section 2.1: az is the default, with en and ru alongside. The
      // device language is used when it is one of the three, and Azerbaijani
      // otherwise.
      locale: null,
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: const [
        AppLocalizations.delegate,
        GlobalMaterialLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
      ],
      localeResolutionCallback: (deviceLocale, supported) {
        if (deviceLocale != null) {
          for (final locale in supported) {
            if (locale.languageCode == deviceLocale.languageCode) {
              return locale;
            }
          }
        }

        return const Locale('az');
      },
    );
  }
}
