import 'package:flutter/material.dart';

import '../../l10n/app_localizations.dart';
import '../../shared/widgets/placeholder_screen.dart';

/// The scheduled prints tab (plan section 8.2). Arrives in step 5.
class ScheduleScreen extends StatelessWidget {
  const ScheduleScreen({super.key});

  @override
  Widget build(BuildContext context) => PlaceholderScreen(
        title: AppLocalizations.of(context).navScheduled,
        icon: Icons.schedule_outlined,
      );
}
