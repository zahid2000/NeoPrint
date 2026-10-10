import 'package:flutter/widgets.dart';
import 'package:go_router/go_router.dart';

import '../features/readiness.dart';
import 'app_router.dart';

final _code = RegExp(r'^[A-Za-z0-9-]{1,40}$');

/// The app route a link into the app opens, or null when [uri] is not one of
/// ours (plan section 8.3, and the screens a notification opens -
/// docs/DECISIONS.md T-146).
///
/// | Link | Opens |
/// | --- | --- |
/// | `neoprint://pay/{id}` | the payment |
/// | `neoprint://kiosk/{code}` | the kiosk's detail |
/// | `neoprint://voucher/{code}` | Balance, with the voucher filled in |
/// | `https://neoprint.az/r/{code}` (or `/r/{code}`) | registration, with the referral code |
/// | `neoprint://jobs/{id}` | home, where the orders are |
/// | `neoprint://schedules/{id}` | the scheduled prints |
/// | `neoprint://wallet` | Balance |
/// | `neoprint://files` | My files |
/// | `neoprint://privacy/export` | Məlumatlarımı yüklə |
/// | `neoprint://notifications` | the notifications |
/// | `neoprint://referral` | Dostunu dəvət et |
/// | `neoprint://shares` | Paylaşılanlar |
/// | `neoprint://share/{NP-id}` | Faylı paylaş, to that user |
///
/// The router's sign-in gates still apply to whatever this returns.
String? appLinkLocation(Uri uri) {
  final segments = uri.pathSegments.where((s) => s.isNotEmpty).toList();

  // The referral link: the site's own address, which the OS hands over as
  // a path (or whole, from a browser).
  final onSite =
      uri.scheme.isEmpty ||
      (uri.scheme == 'https' && uri.host == 'neoprint.az');
  if (onSite &&
      segments.length == 2 &&
      segments[0] == 'r' &&
      _code.hasMatch(segments[1])) {
    return '${Routes.register}?ref=${segments[1].toUpperCase()}';
  }

  if (uri.scheme != 'neoprint') {
    return null;
  }

  final one = segments.length == 1 && _code.hasMatch(segments.single)
      ? segments.single
      : null;

  return switch (uri.host) {
    'pay' when one != null => '/pay/$one',
    'kiosk' when one != null => '/kiosk/${one.toUpperCase()}',
    'voucher' when one != null =>
      '${Routes.wallet}?voucher=${one.toUpperCase()}',
    'jobs' => Routes.home,
    'schedules' => Routes.scheduled,
    'wallet' => Routes.wallet,
    'files' => Routes.files,
    'privacy' when segments.length == 1 && segments.single == 'export' =>
      Routes.privacyExport,
    'notifications' => Routes.notifications,
    'referral' when Readiness.referral => Routes.referral,
    'shares' when Readiness.fileSharing => Routes.shares,
    'share' when Readiness.fileSharing && one != null =>
      '${Routes.shareSend}?to=${one.toUpperCase()}',
    _ => null,
  };
}

/// The bottom-navigation tabs (plan section 8.1): switched to, never pushed -
/// pushing a tab's route would stack a second, empty copy of the tab shell.
const _tabs = {
  Routes.home,
  Routes.kiosks,
  Routes.files,
  Routes.scheduled,
  Routes.profile,
};

/// Opens a route from [appLinkLocation]: a tab is switched to, any other
/// screen goes on top of where the user is, so back returns there.
void openAppLocation(BuildContext context, String location) {
  final router = GoRouter.of(context);

  // Already there - the banner's "Aç" on the screen it points to.
  if (router.routerDelegate.currentConfiguration.uri.path ==
      Uri.parse(location).path) {
    return;
  }

  if (_tabs.contains(Uri.parse(location).path)) {
    router.go(location);
  } else {
    router.push(location);
  }
}
