import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../auth/auth_controller.dart';
import '../network/api_client.dart';
import '../push/push_service.dart';
import '../storage/secure_storage.dart';
import 'app_language.dart';

/// The language the app shows: one the user picked in the profile, or the
/// phone's (T-152).
@immutable
final class AppLanguage {
  const AppLanguage({required this.choice, required this.phone});

  /// `az`, `en` or `ru`; null to follow the phone.
  final String? choice;

  /// The phone's language, as the app would show it.
  final String phone;

  String get code => choice ?? phone;

  bool get followsPhone => choice == null;

  @override
  bool operator ==(Object other) =>
      other is AppLanguage && other.choice == choice && other.phone == phone;

  @override
  int get hashCode => Object.hash(choice, phone);
}

/// `PUT users/me`: the account's language.
class AccountLanguageApi {
  AccountLanguageApi(this._api);

  final ApiClient _api;

  Future<void> save(String language) => _api.send(
    method: 'PUT',
    path: 'api/users/me',
    body: {'language': language},
    parse: (_) {},
  );
}

final accountLanguageApiProvider = Provider<AccountLanguageApi>(
  (ref) => AccountLanguageApi(ref.watch(apiClientProvider)),
);

final appLanguageProvider =
    NotifierProvider<AppLanguageController, AppLanguage>(
      AppLanguageController.new,
    );

/// Keeps the app's language and the account's in step (docs/DECISIONS.md
/// T-152).
///
/// The account's language wins at sign-in: a choice made on another phone
/// follows the user here. "The phone's language", chosen on this device,
/// stays the phone's: the account is told when the phone's language changes.
class AppLanguageController extends Notifier<AppLanguage> {
  static const _system = 'system';

  var _loaded = Completer<void>();
  ({String owner, String choice})? _stored;

  /// The account state handled last: the start-up read and the listener can
  /// both bring the same one.
  ({String id, String language})? _handled;

  SecureStorage get _storage => ref.read(secureStorageProvider);

  @override
  AppLanguage build() {
    ref.listen(
      currentUserProvider.select(
        (user) => user == null ? null : (id: user.id, language: user.language),
      ),
      (_, account) => unawaited(_accountChanged(account)),
    );

    final lifecycle = AppLifecycleListener(
      onResume: () => unawaited(_phoneMaybeChanged()),
    );
    ref.onDispose(lifecycle.dispose);

    _loaded = Completer<void>();
    unawaited(_load());
    return _apply(AppLanguage(choice: null, phone: phoneLanguageCode()));
  }

  /// Picks a language in the profile: [code], or null for the phone's. The
  /// screen switches at once; if the account cannot be told, it switches back
  /// and the error goes to the caller.
  Future<void> choose(String? code) async {
    final user = ref.read(currentUserProvider);
    final previous = state;
    state = _apply(AppLanguage(choice: code, phone: phoneLanguageCode()));

    if (user == null) {
      return;
    }

    try {
      await ref.read(accountLanguageApiProvider).save(state.code);
    } on ApiException {
      state = _apply(previous);
      rethrow;
    }

    await _remember(user.id, code ?? _system);
    ref.read(authControllerProvider.notifier).languageSaved(state.code);
  }

  Future<void> _load() async {
    try {
      final raw = await _storage.read(SecureStorageKeys.language);
      final parts = raw?.split('|');
      if (parts != null && parts.length == 2) {
        _stored = (owner: parts[0], choice: parts[1]);
      }
    } finally {
      _loaded.complete();
    }

    final user = ref.read(currentUserProvider);
    if (user != null) {
      await _accountChanged((id: user.id, language: user.language));
    }
  }

  Future<void> _accountChanged(({String id, String language})? account) async {
    await _loaded.future;

    if (account == null) {
      // Signed out: the sign-in screen stays in the language it had.
      _handled = null;
      return;
    }

    if (account == _handled) {
      return;
    }
    _handled = account;

    final phone = phoneLanguageCode();
    final stored = _stored;
    final followsPhone = stored != null && stored.owner == account.id
        ? stored.choice == _system
        // First time this account on this phone: "the phone's language" when
        // the account's is the phone's anyway.
        : account.language == phone;

    if (followsPhone) {
      state = _apply(AppLanguage(choice: null, phone: phone));
      await _remember(account.id, _system);
      if (account.language != phone) {
        await _tellAccount(phone);
      }
    } else {
      state = _apply(AppLanguage(choice: account.language, phone: phone));
      await _remember(account.id, account.language);
    }
  }

  Future<void> _phoneMaybeChanged() async {
    final phone = phoneLanguageCode();
    if (phone == state.phone) {
      return;
    }

    state = _apply(AppLanguage(choice: state.choice, phone: phone));

    final user = ref.read(currentUserProvider);
    if (state.followsPhone && user != null && user.language != phone) {
      await _tellAccount(phone);
    }
  }

  /// Best effort: the next start tries again.
  Future<void> _tellAccount(String language) async {
    try {
      await ref.read(accountLanguageApiProvider).save(language);
      ref.read(authControllerProvider.notifier).languageSaved(language);
    } on ApiException {
      // Emails stay in the old language until then.
    }
  }

  Future<void> _remember(String owner, String choice) async {
    if (_stored case (owner: final o, choice: final c)
        when o == owner && c == choice) {
      return;
    }

    _stored = (owner: owner, choice: choice);
    await _storage.write(SecureStorageKeys.language, '$owner|$choice');
  }

  AppLanguage _apply(AppLanguage language) {
    chosenLanguageCode = language.choice;
    ref.read(pushRegistrationProvider).language = language.code;
    return language;
  }
}
