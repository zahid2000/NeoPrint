import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import 'user_location.dart';

/// What the user does about a missing location (T-428), never a notice
/// alone: "Məkanı aktivləşdir" opens the phone's location settings, "İcazə
/// ver" asks for the permission, "Tənzimləmələri aç" opens the app's
/// settings for a permission refused for good; back from the settings,
/// [retry] looks again by itself.
class LocationFixButton extends ConsumerWidget {
  const LocationFixButton({
    super.key,
    required this.problem,
    required this.retry,
    this.label,
  });

  final LocationProblem? problem;
  final Future<void> Function() retry;

  /// The words when nothing is wrong yet - "Yaxın kioskları göstər".
  final String? label;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final (IconData icon, String text) = switch (problem) {
      LocationProblem.serviceOff => (
        Icons.location_on_outlined,
        l10n.locationTurnOn,
      ),
      LocationProblem.deniedForever => (
        Icons.settings_outlined,
        l10n.locationOpenSettings,
      ),
      LocationProblem.denied => (Icons.my_location, l10n.locationAllow),
      LocationProblem.unavailable => (Icons.refresh, l10n.locationTryAgain),
      LocationProblem.notAsked ||
      null => (Icons.my_location, label ?? l10n.locationAllow),
    };

    return OutlinedButton.icon(
      // As wide as its words: it shares a line with another button.
      style: OutlinedButton.styleFrom(
        minimumSize: const Size(0, Sizes.touchTarget),
      ),
      onPressed: () =>
          fixLocation(ref.read(locationSettingsProvider), problem, retry),
      icon: Icon(icon),
      label: Text(text),
    );
  }
}
