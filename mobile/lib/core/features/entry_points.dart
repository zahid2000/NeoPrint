import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../features/prepared/prepared_repository.dart';
import '../../features/schedule/schedule_repository.dart';
import 'feature_flags.dart';
import 'readiness.dart';

/// Whether "Planlı çap" is offered. Switched off, it stays while a paid
/// schedule is still waiting (docs/DECISIONS.md, T-148) - the home shortcut,
/// the Print tab and the profile all ask here, so they agree.
final scheduledVisibleProvider = Provider.autoDispose<bool>(
  (ref) =>
      Readiness.scheduledPrint &&
      (ref.watch(featureOnProvider(FeatureKey.scheduledPrint)) ||
          (ref
                  .watch(schedulesProvider)
                  .valueOrNull
                  ?.items
                  .any((item) => item.status.isActive) ??
              false)),
);

/// Whether "Mənim QR kodum" and "Hazırladıqlarım" are offered. Switched off,
/// they stay while something prepared or paid is still there to collect
/// (docs/DECISIONS.md, T-148).
final personalQrVisibleProvider = Provider.autoDispose<bool>(
  (ref) =>
      Readiness.personalQr &&
      (ref.watch(featureOnProvider(FeatureKey.userQrPrint)) ||
          (ref.watch(preparedPrintsProvider).valueOrNull?.isNotEmpty ?? false)),
);
