import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/features/home/near_kiosks.dart';
import 'package:neoprint/features/kiosk_search/kiosk_models.dart';

KioskSummary kiosk(String code, KioskStatus status, double km) => KioskSummary(
  id: code,
  code: code,
  name: code,
  address: 'Bakı',
  lat: 40.4,
  lng: 49.8,
  status: status,
  distanceKm: km,
);

void main() {
  // The home screen's card (T-367): the nearest kiosk that works now.
  test('the card shows the nearest working kiosk, the rest are listed nearest first', () {
    final near = NearKiosks(
      kiosks: [
        kiosk('KSK-1', KioskStatus.offline, 0.2),
        kiosk('KSK-2', KioskStatus.maintenance, 0.4),
        kiosk('KSK-3', KioskStatus.online, 0.9),
        kiosk('KSK-4', KioskStatus.online, 1.5),
      ],
    );

    expect(near.nearestWorking?.code, 'KSK-3');
    expect(near.others.map((k) => k.code), ['KSK-1', 'KSK-2', 'KSK-4']);
  });

  test(
    'with none working near there is no card kiosk and every kiosk is listed',
    () {
      final near = NearKiosks(
        kiosks: [
          kiosk('KSK-1', KioskStatus.offline, 0.2),
          kiosk('KSK-2', KioskStatus.paused, 0.4),
        ],
      );

      expect(near.nearestWorking, isNull);
      expect(near.others.map((k) => k.code), ['KSK-1', 'KSK-2']);
    },
  );

  test('nothing near: nothing on the card, nothing listed', () {
    const near = NearKiosks();

    expect(near.nearestWorking, isNull);
    expect(near.others, isEmpty);
  });

  test('the card takes the nearest working kiosk however far (T-369, T-434) '
      'and lists at most $nearKioskOthers others', () {
    final near = NearKiosks(
      kiosks: [
        for (var i = 0; i < 15; i++)
          kiosk('KSK-$i', KioskStatus.offline, i + 0.5),
        kiosk('KSK-far', KioskStatus.online, 12.4),
      ],
    );

    expect(near.nearestWorking?.code, 'KSK-far');
    expect(near.others, hasLength(nearKioskOthers));
  });
}
