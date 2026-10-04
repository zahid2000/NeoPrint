import 'package:flutter/services.dart';

const _channel = MethodChannel('az.neoprint/browser');

Future<bool> openInBrowser(Uri url) async {
  try {
    return await _channel.invokeMethod<bool>('open', {'url': url.toString()}) ??
        false;
  } on PlatformException {
    return false;
  } on MissingPluginException {
    return false;
  }
}
