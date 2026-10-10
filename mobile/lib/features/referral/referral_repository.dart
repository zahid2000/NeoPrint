import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/network/api_client.dart';

/// The user's referral code and what it earned (`GET api/referrals/me`).
@immutable
class MyReferral {
  const MyReferral({
    required this.code,
    required this.link,
    required this.invited,
    required this.rewarded,
    required this.waiting,
    required this.bonusPagesEarned,
    required this.bonusPagesPerInvite,
    required this.inviteeBonusPages,
  });

  factory MyReferral.fromJson(Map<String, dynamic> json) => MyReferral(
    code: json['code'] as String? ?? '',
    link: json['link'] as String? ?? '',
    invited: json['invited'] as int? ?? 0,
    rewarded: json['rewarded'] as int? ?? 0,
    waiting: json['waiting'] as int? ?? 0,
    bonusPagesEarned: json['bonusPagesEarned'] as int? ?? 0,
    bonusPagesPerInvite: json['bonusPagesPerInvite'] as int? ?? 0,
    inviteeBonusPages: json['inviteeBonusPages'] as int? ?? 0,
  );

  final String code;

  /// `https://neoprint.az/r/{code}`: opens the register screen with the code.
  final String link;

  /// Everyone who registered with the code.
  final int invited;

  /// Those whose first paid print brought the bonus.
  final int rewarded;

  /// Registered, their first paid print still to come.
  final int waiting;

  final int bonusPagesEarned;

  /// What an invitation brings now.
  final int bonusPagesPerInvite;

  /// What the invited friend gets.
  final int inviteeBonusPages;
}

final referralRepositoryProvider = Provider<ReferralRepository>(
  (ref) => ReferralRepository(ref.watch(apiClientProvider)),
);

/// The numbers on "Dostunu dəvət et"; a different account starts from its own.
final myReferralProvider = FutureProvider.autoDispose<MyReferral>((ref) {
  ref.watch(currentUserProvider.select((user) => user?.id));

  return ref.watch(referralRepositoryProvider).mine();
});

/// The referral programme (plan section 6, `referrals`).
class ReferralRepository {
  ReferralRepository(this._api);

  final ApiClient _api;

  Future<MyReferral> mine() => _api.send(
    method: 'GET',
    path: 'api/referrals/me',
    parse: (json) => MyReferral.fromJson(json as Map<String, dynamic>),
  );

  /// The link as a QR code, drawn by the server.
  Future<Uint8List> qr() => _api.send(
    method: 'GET',
    path: 'api/referrals/me/qr.png',
    responseType: ResponseType.bytes,
    parse: (data) => Uint8List.fromList(data as List<int>),
  );
}
