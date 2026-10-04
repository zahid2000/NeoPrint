import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'browser_io.dart'
    if (dart.library.js_interop) 'browser_web.dart'
    as impl;

/// Opens an http(s) link outside the app - the system browser on a phone, a
/// new tab on the web. False when nothing could open it.
///
/// Our own platform channel rather than a package (plan section 2.0): the
/// "Məlumatlarımı yüklə" archive is too large to pass through the app.
Future<bool> openInBrowser(Uri url) => impl.openInBrowser(url);

/// [openInBrowser] for the screens, so a test can stand in for the browser.
final browserProvider = Provider<Future<bool> Function(Uri url)>(
  (_) => openInBrowser,
);
