import 'dart:math';

/// The header the backend reads and echoes (`docs/OBSERVABILITY.md`).
const String correlationIdHeader = 'X-Correlation-Id';

final Random _random = Random();

/// A fresh correlation id, in the same shape the backend generates.
///
/// Thirty-two hexadecimal characters, so a line from the app and a line from
/// the server look alike in a log. It is a log key and authorises nothing, so
/// an ordinary random source is the right one - a secure one would cost
/// entropy the platform needs elsewhere for no benefit.
String newCorrelationId() {
  final buffer = StringBuffer();

  for (var i = 0; i < 8; i++) {
    buffer.write(_random.nextInt(1 << 16).toRadixString(16).padLeft(4, '0'));
  }

  return buffer.toString();
}
