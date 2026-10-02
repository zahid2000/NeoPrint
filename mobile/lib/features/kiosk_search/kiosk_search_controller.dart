import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/network/api_client.dart';
import 'kiosk_models.dart';
import 'kiosk_repository.dart';
import 'user_location.dart';

final kioskRepositoryProvider = Provider<KioskRepository>(
  (ref) => KioskRepository(ref.watch(apiClientProvider)),
);

enum KioskView { list, map }

/// The radii "near me" offers, in kilometres.
const kioskRadii = [1, 3, 10];

/// What the kiosks tab is showing: the text, whether "near me" is on and how
/// far, and the list or the map. The list and the map show the same result.
final class KioskSearchState {
  const KioskSearchState({
    this.text = '',
    this.nearMe = false,
    this.radiusKm = 3,
    this.view = KioskView.list,
    this.location,
    this.locating = false,
  });

  final String text;
  final bool nearMe;
  final int radiusKm;
  final KioskView view;

  /// The last answer to "where am I"; null until "near me" is first chosen.
  final LocationResult? location;
  final bool locating;

  /// Where to search near: only while "near me" is on and a place is known.
  GeoPoint? get near => nearMe && location is LocationFound
      ? (location! as LocationFound).point
      : null;

  KioskSearchState copyWith({
    String? text,
    bool? nearMe,
    int? radiusKm,
    KioskView? view,
    LocationResult? location,
    bool? locating,
  }) => KioskSearchState(
    text: text ?? this.text,
    nearMe: nearMe ?? this.nearMe,
    radiusKm: radiusKm ?? this.radiusKm,
    view: view ?? this.view,
    location: location ?? this.location,
    locating: locating ?? this.locating,
  );
}

class KioskSearchController extends Notifier<KioskSearchState> {
  @override
  KioskSearchState build() => const KioskSearchState();

  void setText(String text) => state = state.copyWith(text: text.trim());

  void setView(KioskView view) => state = state.copyWith(view: view);

  void setRadius(int km) => state = state.copyWith(radiusKm: km);

  /// The next radius up, for "widen the radius"; the largest stays.
  void widenRadius() {
    final next = kioskRadii.where((km) => km > state.radiusKm);
    if (next.isNotEmpty) {
      setRadius(next.first);
    }
  }

  /// Turns "near me" on - asking for the location each time, so a permission
  /// given in the settings meanwhile is picked up - or off.
  Future<void> setNearMe(bool on) async {
    if (!on) {
      state = state.copyWith(nearMe: false);
      return;
    }

    state = state.copyWith(nearMe: true, locating: true);
    final location = await ref.read(userLocationProvider).current();
    state = state.copyWith(location: location, locating: false);
  }
}

final kioskSearchProvider =
    NotifierProvider<KioskSearchController, KioskSearchState>(
      KioskSearchController.new,
    );

/// The kiosks for the current search. While the location is being found the
/// last result stays on screen rather than the list jumping twice.
final kioskResultsProvider = FutureProvider.autoDispose<List<KioskSummary>>((
  ref,
) {
  final search = ref.watch(kioskSearchProvider);
  final near = search.near;

  return ref
      .watch(kioskRepositoryProvider)
      .search(
        text: search.text,
        near: near,
        radiusKm: near == null ? null : search.radiusKm.toDouble(),
      );
});

/// One kiosk, by id or code.
final kioskDetailProvider = FutureProvider.autoDispose
    .family<KioskDetail, String>(
      (ref, idOrCode) => ref.watch(kioskRepositoryProvider).detail(idOrCode),
    );

/// Whether an error is "this kiosk is not there", which reads as an empty
/// state rather than a failure.
bool isNotFound(Object error) =>
    error is ApiException && error.statusCode == 404;
