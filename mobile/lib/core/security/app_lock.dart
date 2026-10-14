import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../auth/auth_controller.dart';
import '../auth/auth_models.dart';
import '../storage/secure_storage.dart';
import '../time/app_clock.dart';
import 'biometrics.dart';

/// Where the app's lock stands (T-270).
enum AppLockStatus {
  /// Nobody is signed in, or it is not decided yet: the lock has nothing to say.
  off,

  /// Signed in, and no PIN is set on this phone for the account: one is set
  /// before anything else (the PIN is mandatory - the owner, 2026-10-14).
  setup,

  /// The PIN (or the fingerprint or face) is asked before the app opens.
  locked,

  /// Open.
  unlocked,
}

/// The lock as the screens need it.
@immutable
final class AppLockState {
  const AppLockState(this.status, {this.biometrics = false});

  final AppLockStatus status;

  /// Whether the fingerprint or face opens the app as well as the PIN.
  final bool biometrics;

  AppLockState copyWith({AppLockStatus? status, bool? biometrics}) =>
      AppLockState(
        status ?? this.status,
        biometrics: biometrics ?? this.biometrics,
      );
}

/// What a PIN typed at the lock came to.
@immutable
final class UnlockResult {
  const UnlockResult._(this.unlocked, this.attemptsLeft);

  const UnlockResult.unlocked() : this._(true, AppLockController.maxFailures);

  const UnlockResult.wrong(int attemptsLeft) : this._(false, attemptsLeft);

  final bool unlocked;

  /// Wrong PINs still allowed before the account is signed out on this phone;
  /// nought means it just was.
  final int attemptsLeft;

  bool get signedOut => !unlocked && attemptsLeft == 0;
}

/// The app's PIN and biometric lock (T-270; the owner's decisions of
/// 2026-10-14, docs/DECISIONS.md):
///
/// - a 4-digit PIN is mandatory: set after signing in, never switched off;
/// - asked when the app starts with a stored session, and when it comes back
///   after more than [lockAfter] in the background;
/// - [maxFailures] wrong PINs in a row sign the account out on this phone;
/// - a fingerprint or a face may open it as well, where the phone has one.
///
/// It is a layer on this phone over the stored session: the server knows
/// nothing of it. The PIN never leaves the phone - it lives in the secure
/// storage (Keystore / Keychain sealed) with the account's id, and so does the
/// count of wrong PINs, so killing the app does not reset it. Signing out, by
/// hand or by the server, forgets all of it: the next account sets its own.
///
/// It follows the auth controller and never the other way round: the auth
/// controller reading a provider that watches it is a circular dependency
/// (T-268).
final class AppLockController extends Notifier<AppLockState> {
  static const pinLength = 4;
  static const maxFailures = 5;
  static const lockAfter = Duration(minutes: 5);

  /// Whether the session being signed in was restored from storage at start -
  /// asked for the PIN - rather than signed in with the password just now.
  bool _restored = false;

  /// When the app went to the background while open.
  DateTime? _hiddenAt;

  SecureStorage get _storage => ref.read(secureStorageProvider);

  @override
  AppLockState build() {
    ref.listen<AuthState>(
      authControllerProvider,
      (previous, next) => unawaited(_follow(next)),
      fireImmediately: true,
    );

    final lifecycle = AppLifecycleListener(onHide: appHidden, onShow: appShown);
    ref.onDispose(lifecycle.dispose);

    return const AppLockState(AppLockStatus.off);
  }

  Future<void> _follow(AuthState auth) async {
    switch (auth.status) {
      case AuthStatus.restoring:
        _restored = true;

      case AuthStatus.signedOut:
        _restored = false;
        _hiddenAt = null;
        if (state.status != AppLockStatus.off) {
          state = const AppLockState(AppLockStatus.off);
        }
        await _forget();

      case AuthStatus.awaitingEmailConfirmation:
      case AuthStatus.awaitingConsents:
        break;

      case AuthStatus.signedIn:
        // Decided once for a sign-in: the profile changing (a language saved)
        // says "signed in" again.
        final user = auth.user?.id;
        if (state.status != AppLockStatus.off || user == null) {
          return;
        }

        final pin = await _pinOf(user);
        final biometrics = pin != null && await _biometricsOn(user);

        state = AppLockState(
          pin == null
              ? AppLockStatus.setup
              : _restored
              ? AppLockStatus.locked
              : AppLockStatus.unlocked,
          biometrics: biometrics,
        );
        _restored = false;
    }
  }

  /// Sets the PIN - the first one, or a new one in place of the old.
  Future<void> setPin(String pin) async {
    final user = _user;
    if (user == null || !isPin(pin)) {
      return;
    }

    await _storage.write(SecureStorageKeys.pin, '$user|$pin');
    await _storage.delete(SecureStorageKeys.pinFailures);
    state = state.copyWith(status: AppLockStatus.unlocked);
  }

  /// Checks a PIN typed at the lock, or as the current one before a change.
  Future<UnlockResult> unlock(String pin) async {
    final user = _user;
    final stored = user == null ? null : await _pinOf(user);

    if (stored != null && _same(stored, pin)) {
      await _storage.delete(SecureStorageKeys.pinFailures);
      state = state.copyWith(status: AppLockStatus.unlocked);
      return const UnlockResult.unlocked();
    }

    final failures =
        (int.tryParse(
              await _storage.read(SecureStorageKeys.pinFailures) ?? '',
            ) ??
            0) +
        1;

    if (failures >= maxFailures) {
      // Somebody who is not the owner may be trying: the session on this
      // phone ends, and the password is needed to come back.
      await ref.read(authControllerProvider.notifier).signOut();
      return const UnlockResult.wrong(0);
    }

    await _storage.write(SecureStorageKeys.pinFailures, '$failures');
    return UnlockResult.wrong(maxFailures - failures);
  }

  /// Opens the app with the fingerprint or face, when that is switched on.
  Future<bool> unlockWithBiometrics({
    required String title,
    required String cancel,
  }) async {
    if (!state.biometrics) {
      return false;
    }

    final ok = await ref
        .read(biometricsProvider)
        .authenticate(title: title, cancel: cancel);

    if (ok) {
      await _storage.delete(SecureStorageKeys.pinFailures);
      state = state.copyWith(status: AppLockStatus.unlocked);
    }

    return ok;
  }

  /// Switches the fingerprint or face on or off for this account on this phone.
  Future<void> setBiometrics(bool on) async {
    final user = _user;
    if (user == null) {
      return;
    }

    if (on) {
      await _storage.write(SecureStorageKeys.pinBiometrics, user);
    } else {
      await _storage.delete(SecureStorageKeys.pinBiometrics);
    }

    state = state.copyWith(biometrics: on);
  }

  /// "PIN-i unutdum": the way back is the password.
  Future<void> forgot() => ref.read(authControllerProvider.notifier).signOut();

  /// The app went to the background.
  void appHidden([DateTime? at]) {
    if (state.status == AppLockStatus.unlocked) {
      _hiddenAt = at ?? appNow();
    }
  }

  /// The app is in front again: locked after a long enough absence. A short
  /// one - choosing a file, taking a photo - asks for nothing.
  void appShown([DateTime? at]) {
    final hidden = _hiddenAt;
    _hiddenAt = null;

    if (hidden != null &&
        state.status == AppLockStatus.unlocked &&
        (at ?? appNow()).difference(hidden) >= lockAfter) {
      state = state.copyWith(status: AppLockStatus.locked);
    }
  }

  static bool isPin(String text) =>
      text.length == pinLength && RegExp(r'^[0-9]+$').hasMatch(text);

  String? get _user => ref.read(authControllerProvider).user?.id;

  /// The PIN set on this phone for this account; one of another account is
  /// none.
  Future<String?> _pinOf(String user) async {
    final stored = await _storage.read(SecureStorageKeys.pin);
    final separator = stored?.lastIndexOf('|') ?? -1;

    if (stored == null ||
        separator < 0 ||
        stored.substring(0, separator) != user) {
      return null;
    }

    final pin = stored.substring(separator + 1);
    return isPin(pin) ? pin : null;
  }

  Future<bool> _biometricsOn(String user) async =>
      await _storage.read(SecureStorageKeys.pinBiometrics) == user &&
      await ref.read(biometricsProvider).available();

  Future<void> _forget() async {
    await _storage.delete(SecureStorageKeys.pin);
    await _storage.delete(SecureStorageKeys.pinFailures);
    await _storage.delete(SecureStorageKeys.pinBiometrics);
  }

  /// Compared in full whatever the first difference: how long the check takes
  /// says nothing about how much of the PIN was right.
  static bool _same(String a, String b) {
    if (a.length != b.length) {
      return false;
    }

    var difference = 0;
    for (var i = 0; i < a.length; i++) {
      difference |= a.codeUnitAt(i) ^ b.codeUnitAt(i);
    }

    return difference == 0;
  }
}

final appLockProvider = NotifierProvider<AppLockController, AppLockState>(
  AppLockController.new,
);
