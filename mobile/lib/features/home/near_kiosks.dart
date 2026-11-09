import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../kiosk_search/kiosk_models.dart';
import '../kiosk_search/kiosk_search_controller.dart';
import '../kiosk_search/user_location.dart';

/// How far the home screen looks for a kiosk.
const nearKioskRadiusKm = 10.0;

/// The kiosks near the user, nearest first, or why there is no list.
final class NearKiosks {
  const NearKiosks({this.kiosks = const [], this.problem});

  final List<KioskSummary> kiosks;

  /// Set when there is no location: no list was asked for.
  final LocationProblem? problem;

  /// The card's kiosk (T-367): the nearest that takes orders now, not
  /// merely the nearest; null when none near does.
  KioskSummary? get nearestWorking {
    for (final kiosk in kiosks) {
      if (kiosk.status == KioskStatus.online) {
        return kiosk;
      }
    }
    return null;
  }

  /// "Yaxınlıqdakı digər kiosklar": the rest, nearest first.
  List<KioskSummary> get others {
    final card = nearestWorking;
    return [
      for (final kiosk in kiosks)
        if (kiosk != card) kiosk,
    ];
  }
}

/// The home screen's nearest kiosks (docs/DESIGN_SYSTEM.md, "Yaxın kiosk
/// kartı"). On its own it only looks when the location permission is already
/// there; [find] is the user's "show me" and asks for it.
class NearKiosksController extends AutoDisposeAsyncNotifier<NearKiosks> {
  @override
  Future<NearKiosks> build() => _load(ask: false);

  Future<void> find() async {
    state = const AsyncLoading();
    state = await AsyncValue.guard(() => _load(ask: true));
  }

  Future<NearKiosks> _load({required bool ask}) async {
    final location = await ref.read(userLocationProvider).current(ask: ask);

    switch (location) {
      case LocationMissing(:final problem):
        return NearKiosks(problem: problem);
      case LocationFound(:final point):
        final kiosks = await ref
            .read(kioskRepositoryProvider)
            .search(near: point, radiusKm: nearKioskRadiusKm);
        return NearKiosks(kiosks: kiosks);
    }
  }
}

final nearKiosksProvider =
    AsyncNotifierProvider.autoDispose<NearKiosksController, NearKiosks>(
      NearKiosksController.new,
    );
