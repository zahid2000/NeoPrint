import 'dart:js_interop';

@JS('window.open')
external JSAny? _open(JSString url, JSString target);

Future<bool> openInBrowser(Uri url) async =>
    _open(url.toString().toJS, '_blank'.toJS) != null;
