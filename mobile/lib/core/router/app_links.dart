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
/// | `neoprint://notifications` | the notifications |
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
    'notifications' => Routes.notifications,
    _ => null,
  };
}
