import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:geolocator/geolocator.dart';

import 'kiosk_repository.dart';

/// Why there is no location, each with its own words on screen: the user
/// fixes each a different way (docs/DESIGN_SYSTEM.md, "Mobil: kiosklar").
enum LocationProblem {
  /// Location is switched off on the device.
  serviceOff,

  /// The user said no this time; asking again may work.
  denied,

  /// The user said "never": only the settings can change it.
  deniedForever,

  /// It was asked for and did not come in time.
  unavailable,
}

/// The user's place, or why there is none.
sealed class LocationResult {
  const LocationResult();
}

final class LocationFound extends LocationResult {
  const LocationFound(this.point);

  final GeoPoint point;
}

final class LocationMissing extends LocationResult {
  const LocationMissing(this.problem);

  final LocationProblem problem;
}

/// Where the user is, asked for only when they choose "near me".
abstract interface class UserLocation {
  Future<LocationResult> current();
}

/// The device's own location through geolocator; on the web, the browser's.
final class DeviceLocation implements UserLocation {
  const DeviceLocation();

  @override
  Future<LocationResult> current() async {
    if (!await Geolocator.isLocationServiceEnabled()) {
      return const LocationMissing(LocationProblem.serviceOff);
    }

    var permission = await Geolocator.checkPermission();

    if (permission == LocationPermission.denied) {
      permission = await Geolocator.requestPermission();
    }

    switch (permission) {
      case LocationPermission.denied:
        return const LocationMissing(LocationProblem.denied);
      case LocationPermission.deniedForever:
        return const LocationMissing(LocationProblem.deniedForever);
      case LocationPermission.whileInUse ||
          LocationPermission.always ||
          LocationPermission.unableToDetermine:
        break;
    }

    try {
      final position = await Geolocator.getCurrentPosition(
        locationSettings: const LocationSettings(
          // A street is close enough to find the nearest kiosk; the precise
          // fix is slower and drains the battery for nothing.
          accuracy: LocationAccuracy.medium,
          timeLimit: Duration(seconds: 15),
        ),
      );

      return LocationFound((lat: position.latitude, lng: position.longitude));
    } on Exception {
      return const LocationMissing(LocationProblem.unavailable);
    }
  }
}

final userLocationProvider = Provider<UserLocation>(
  (_) => const DeviceLocation(),
);
