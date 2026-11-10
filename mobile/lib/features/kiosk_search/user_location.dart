import 'dart:async';

import 'package:flutter/foundation.dart';
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

  /// Not given yet, and this time the app did not ask: the home screen only
  /// looks when the permission is already there (T-338).
  notAsked,
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
  /// With [ask] false a permission not given yet is not asked for.
  Future<LocationResult> current({bool ask = true});
}

/// The device's own location through geolocator; on the web, the browser's.
final class DeviceLocation implements UserLocation {
  const DeviceLocation();

  /// How long the home screen waits to learn whether it may look (T-400):
  /// past it the card offers the button, which asks again.
  static const checkLimit = Duration(seconds: 3);

  /// A last known place this recent is used at once (T-400): a cold GPS
  /// fix took up to the time limit while the card only spun.
  static const lastKnownAge = Duration(minutes: 10);

  @override
  Future<LocationResult> current({bool ask = true}) async {
    LocationPermission permission;
    try {
      // Without asking, nothing is waited for long: the card's button is
      // the way in.
      final check = () async {
        if (!await Geolocator.isLocationServiceEnabled()) {
          return null;
        }
        return Geolocator.checkPermission();
      }();
      final checked = ask ? await check : await check.timeout(checkLimit);
      if (checked == null) {
        return const LocationMissing(LocationProblem.serviceOff);
      }
      permission = checked;
    } on TimeoutException {
      return const LocationMissing(LocationProblem.notAsked);
    }

    if (permission == LocationPermission.denied) {
      if (!ask) {
        return const LocationMissing(LocationProblem.notAsked);
      }
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

    if (!kIsWeb) {
      try {
        final last = await Geolocator.getLastKnownPosition();
        if (last != null &&
            DateTime.now().difference(last.timestamp) < lastKnownAge) {
          return LocationFound((lat: last.latitude, lng: last.longitude));
        }
      } on Exception {
        // No last place: a fresh one is asked for below.
      }
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
