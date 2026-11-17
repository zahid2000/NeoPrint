import 'package:cupertino_ui/cupertino_ui.dart'
    show GlobalCupertinoLocalizations;
import 'package:flutter/widgets.dart';
import 'package:flutter_localizations/flutter_localizations.dart'
    show GlobalWidgetsLocalizations;
import 'package:material_ui/material_ui.dart' show GlobalMaterialLocalizations;

import '../../l10n/app_localizations.dart';
import 'material_localizations.dart';

/// Every localization the app needs, in one list (T-444).
///
/// Since Flutter 3.50 the Material and Cupertino widgets live in
/// `material_ui` and `cupertino_ui`, and look up those packages' own
/// localization types. The generated [AppLocalizations.localizationsDelegates]
/// still names `flutter_localizations`' older ones, which those widgets no
/// longer find - so the app and every test use this list instead.
const List<LocalizationsDelegate<dynamic>> appLocalizationsDelegates = [
  AppLocalizations.delegate,
  AppMaterialLocalizationsDelegate(),
  GlobalMaterialLocalizations.delegate,
  GlobalWidgetsLocalizations.delegate,
  GlobalCupertinoLocalizations.delegate,
];
