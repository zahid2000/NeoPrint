import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../auth/auth_controller.dart';
import '../network/api_client.dart';

/// The flag keys the app acts on (plan section 2.16, backend `MvpFlags`).
abstract final class FeatureKey {
  static const mobilePaymentAtKiosk = 'mobile_payment_at_kiosk';
  static const userQrPrint = 'user_qr_print';
  static const mobileSessionPrint = 'mobile_session_print';
  static const scheduledPrint = 'scheduled_print';
  static const promoCodes = 'promo_codes';
  static const dataExport = 'data_export';
  static const fileSharing = 'file_sharing';
}

/// The backend's answer to "which features are on for me" (`GET api/features`).
///
/// A flag that is off closes only new usage (docs/DECISIONS.md, T-148): the
/// way in is hidden, while what the user already paid for stays on screen.
/// [Readiness] says whether a part exists in this build at all; a flag then
/// decides per user on top of it.
@immutable
final class FeatureFlags {
  const FeatureFlags(this._values);

  /// Before the first answer: everything on, as the server treats an unknown
  /// key. Hiding first and showing later would make buttons jump in.
  static const unknown = FeatureFlags({});

  final Map<String, bool> _values;

  /// Whether [key] is on. A key the server did not send is on: a flag is
  /// removed from the catalogue once its feature is permanent.
  bool isOn(String key) => _values[key] ?? true;

  @override
  bool operator ==(Object other) =>
      other is FeatureFlags &&
      other._values.length == _values.length &&
      other._values.entries.every((e) => _values[e.key] == e.value);

  @override
  int get hashCode => Object.hashAllUnordered(
    _values.entries.map((e) => Object.hash(e.key, e.value)),
  );
}

final class FeatureFlagsRepository {
  FeatureFlagsRepository(this._api);

  final ApiClient _api;

  Future<FeatureFlags> load() => _api.send(
    method: 'GET',
    path: 'api/features',
    parse: (json) => FeatureFlags({
      for (final entry in (json as Map<String, dynamic>).entries)
        if (entry.value is bool) entry.key: entry.value as bool,
    }),
  );
}

final featureFlagsRepositoryProvider = Provider<FeatureFlagsRepository>(
  (ref) => FeatureFlagsRepository(ref.watch(apiClientProvider)),
);

/// The flags for the signed-in user (a percentage rollout depends on who).
/// Read again on sign-in, when the app comes back to the front, and when the
/// server refuses a request because a feature is off.
final featureFlagsProvider =
    NotifierProvider<FeatureFlagsController, FeatureFlags>(
      FeatureFlagsController.new,
    );

/// Whether one feature is on, for a widget that needs just that.
final featureOnProvider = Provider.family<bool, String>(
  (ref, key) => ref.watch(featureFlagsProvider.select((f) => f.isOn(key))),
);

class FeatureFlagsController extends Notifier<FeatureFlags> {
  AppLifecycleListener? _lifecycle;

  @override
  FeatureFlags build() {
    ref.listen(
      currentUserProvider.select((user) => user?.id),
      (_, __) => unawaited(refresh()),
    );

    ref.read(apiClientProvider).onFeatureOff = () => unawaited(refresh());

    _lifecycle = AppLifecycleListener(onResume: () => unawaited(refresh()));
    ref.onDispose(() => _lifecycle?.dispose());

    Future.microtask(refresh);
    return FeatureFlags.unknown;
  }

  Future<void> refresh() async {
    try {
      state = await ref.read(featureFlagsRepositoryProvider).load();
    } on ApiException {
      // Unreachable or refused: keep what is known. The server still closes
      // whatever is off, so a stale "on" costs a refused request, not money.
    }
  }
}
