import 'package:flutter/material.dart';

import '../../l10n/app_localizations.dart';
import '../../shared/widgets/placeholder_screen.dart';

/// The kiosks tab (plan section 8.2). The list, the map and the kiosk detail
/// arrive in step 2.
class KioskSearchScreen extends StatelessWidget {
  const KioskSearchScreen({super.key});

  @override
  Widget build(BuildContext context) => PlaceholderScreen(
    title: AppLocalizations.of(context).navKiosks,
    icon: Icons.place_outlined,
  );
}
