import 'package:flutter/material.dart';

import '../../l10n/app_localizations.dart';
import '../../shared/widgets/placeholder_screen.dart';

/// The files tab (plan section 8.2). Upload, preview and editing arrive in
/// step 2.
class FilesScreen extends StatelessWidget {
  const FilesScreen({super.key});

  @override
  Widget build(BuildContext context) => PlaceholderScreen(
        title: AppLocalizations.of(context).navFiles,
        icon: Icons.folder_outlined,
      );
}
