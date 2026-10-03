import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/network/api_client.dart';
import '../../core/network/idempotency.dart';
import '../../core/realtime/user_hub_provider.dart';
import '../session/session_models.dart';
import 'schedule_models.dart';

/// Scheduled printing (docs/API.md "Planlanmış çap").
class ScheduleRepository {
  ScheduleRepository(this._api);

  final ApiClient _api;

  Future<List<ScheduledPrint>> list() => _api.send(
    method: 'GET',
    path: 'api/schedules',
    parse: (json) => [
      for (final item
          in (json as Map<String, dynamic>)['items'] as List<dynamic>)
        ScheduledPrint.fromJson(item as Map<String, dynamic>),
    ],
  );

  /// [expectedAmount] is the price shown; the server refuses with
  /// `printJobs.priceChanged` when the kiosk's prices changed since.
  Future<PlacedSchedule> create({
    required String kiosk,
    required String fileId,
    required OrderOptions options,
    required DateTime scheduledAt,
    required double expectedAmount,
    required String idempotencyKey,
  }) => _api.send(
    method: 'POST',
    path: 'api/schedules',
    headers: {idempotencyKeyHeader: idempotencyKey},
    body: {
      'kiosk': kiosk,
      'fileId': fileId,
      'options': options.toJson(),
      'scheduledAt': scheduledAt.toUtc().toIso8601String(),
      'expectedAmount': expectedAmount,
    },
    parse: (json) => PlacedSchedule.fromJson(json as Map<String, dynamic>),
  );

  /// "Vaxtı dəyiş": back to waiting, at the new time.
  Future<ScheduledPrint> reschedule(String id, DateTime scheduledAt) =>
      _api.send(
        method: 'PUT',
        path: 'api/schedules/$id/time',
        body: {'scheduledAt': scheduledAt.toUtc().toIso8601String()},
        parse: (json) => ScheduledPrint.fromJson(json as Map<String, dynamic>),
      );

  /// "Çapa ver": into the kiosk's queue now.
  Future<ScheduledPrint> printNow(String id) => _api.send(
    method: 'POST',
    path: 'api/schedules/$id/print-now',
    headers: {idempotencyKeyHeader: newIdempotencyKey()},
    parse: (json) => ScheduledPrint.fromJson(json as Map<String, dynamic>),
  );

  /// Cancels it; what was paid goes back to the wallet.
  Future<void> cancel(String id) =>
      _api.send(method: 'DELETE', path: 'api/schedules/$id', parse: (_) {});
}

final scheduleRepositoryProvider = Provider<ScheduleRepository>(
  (ref) => ScheduleRepository(ref.watch(apiClientProvider)),
);

/// The user's scheduled prints, newest first. Read again when the hub says
/// one of them, its order or its money moved on.
final schedulesProvider =
    AsyncNotifierProvider<SchedulesController, List<ScheduledPrint>>(
      SchedulesController.new,
    );

class SchedulesController extends AsyncNotifier<List<ScheduledPrint>> {
  static const _news = {
    'ScheduleDue',
    'JobStatusChanged',
    'PaymentStatusChanged',
    'RefundStatusChanged',
  };

  ScheduleRepository get _repository => ref.read(scheduleRepositoryProvider);

  @override
  Future<List<ScheduledPrint>> build() {
    final news = ref
        .watch(userHubEventsProvider)
        .where((event) => _news.contains(event.target))
        .listen((_) => unawaited(refresh()));
    final back = ref
        .watch(userHubReconnectedProvider)
        .listen((_) => unawaited(refresh()));

    ref.onDispose(() {
      unawaited(news.cancel());
      unawaited(back.cancel());
    });

    return _repository.list();
  }

  Future<void> refresh() async {
    state = await AsyncValue.guard(_repository.list);
  }

  Future<void> reschedule(String id, DateTime at) async {
    await _repository.reschedule(id, at);
    await refresh();
  }

  Future<void> printNow(String id) async {
    await _repository.printNow(id);
    await refresh();
  }

  Future<void> cancel(String id) async {
    await _repository.cancel(id);
    await refresh();
  }
}
