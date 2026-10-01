import 'dart:async';
import 'dart:convert';

import 'package:web_socket_channel/web_socket_channel.dart';

/// One message the server sent on the user hub: what changed and the
/// current state of it (plan section 6, `/hubs/user`).
final class HubEvent {
  const HubEvent(this.target, this.payload);

  /// `JobStatusChanged`, `PaymentStatusChanged` or `RefundStatusChanged`.
  final String target;

  final Map<String, dynamic> payload;

  @override
  String toString() => 'HubEvent($target, $payload)';
}

/// The few things the hub needs from a WebSocket, so the connection logic can
/// be tested without a server.
abstract interface class HubSocket {
  Stream<String> get messages;

  void send(String text);

  Future<void> close();
}

/// Opens a [HubSocket] on a `ws://` or `wss://` address.
typedef HubSocketFactory = Future<HubSocket> Function(Uri uri);

/// The SignalR JSON protocol ends every message with this character.
const recordSeparator = '\u001e';

/// The messages in one WebSocket frame, as JSON objects.
///
/// A frame can carry several messages, and the handshake answer is an empty
/// object; anything that is not a JSON object is skipped rather than ending
/// the connection.
List<Map<String, dynamic>> parseHubFrame(String frame) {
  final messages = <Map<String, dynamic>>[];

  for (final part in frame.split(recordSeparator)) {
    if (part.trim().isEmpty) {
      continue;
    }

    try {
      final decoded = jsonDecode(part);
      if (decoded is Map<String, dynamic>) {
        messages.add(decoded);
      }
    } on FormatException {
      // A malformed message is not worth losing the connection over.
    }
  }

  return messages;
}

/// `http://host:5080/` → `ws://host:5080/hubs/user?id=…&access_token=…`.
///
/// The token goes in the query because a browser cannot put a header on a
/// WebSocket request; the API reads it from there on hub paths only.
Uri hubSocketUri(
  String baseUrl,
  String hubPath, {
  required String connectionToken,
  required String accessToken,
}) {
  final base = Uri.parse(baseUrl);
  final scheme = base.scheme == 'https' ? 'wss' : 'ws';

  return base
      .replace(scheme: scheme)
      .resolve(hubPath)
      .replace(
        queryParameters: {'id': connectionToken, 'access_token': accessToken},
      );
}

/// A connection to `/hubs/user` that keeps itself open.
///
/// Our own small SignalR client (plan section 2.0: no SignalR package): it
/// negotiates, opens the WebSocket, sends the JSON handshake, answers the
/// server's keep-alive and reconnects with growing pauses when the network
/// drops. Messages are nudges, not records - after a reconnection
/// [reconnected] fires so screens read again what they may have missed.
final class UserHubConnection {
  UserHubConnection({
    required this.baseUrl,
    required this.negotiate,
    required this.accessToken,
    required this.connect,
    this.hubPath = 'hubs/user',
    this.pingEvery = const Duration(seconds: 15),
    this.serverTimeout = const Duration(seconds: 30),
    this.retryDelays = const [
      Duration.zero,
      Duration(seconds: 2),
      Duration(seconds: 10),
      Duration(seconds: 30),
    ],
  });

  final String baseUrl;
  final String hubPath;

  /// POSTs `{hubPath}/negotiate?negotiateVersion=1` and returns the
  /// connection token.
  final Future<String> Function() negotiate;

  /// A token that is still valid, or null when signed out.
  final Future<String?> Function() accessToken;

  final HubSocketFactory connect;

  /// How often the client tells the server it is still there; the server
  /// drops a client it has not heard from for 30 seconds.
  final Duration pingEvery;

  /// A server that sends nothing for this long is gone; its own pings come
  /// every 15 seconds.
  final Duration serverTimeout;

  /// The pause before each reconnection attempt; the last one repeats.
  final List<Duration> retryDelays;

  final _events = StreamController<HubEvent>.broadcast();
  final _reconnected = StreamController<void>.broadcast();

  HubSocket? _socket;
  Completer<void>? _wait;
  bool _running = false;
  bool _connectedOnce = false;
  bool _connected = false;

  Stream<HubEvent> get events => _events.stream;

  /// Fires each time the connection comes back after being lost.
  Stream<void> get reconnected => _reconnected.stream;

  bool get isConnected => _connected;

  /// Starts connecting and keeps the connection up until [stop].
  void start() {
    if (_running) {
      return;
    }

    _running = true;
    unawaited(_run());
  }

  Future<void> stop() async {
    _running = false;

    final wait = _wait;
    if (wait != null && !wait.isCompleted) {
      wait.complete();
    }

    await _socket?.close();
    _socket = null;
    _connected = false;
  }

  /// Closes the streams; the connection cannot be started again.
  Future<void> dispose() async {
    await stop();
    await _events.close();
    await _reconnected.close();
  }

  Future<void> _run() async {
    var attempt = 0;

    while (_running) {
      try {
        if (await _session()) {
          attempt = 0;
        }
      } on Object {
        // Offline, the server restarting, a refused token: all are retried.
      }

      _connected = false;

      if (!_running) {
        break;
      }

      await _pause(retryDelays[attempt.clamp(0, retryDelays.length - 1)]);
      attempt++;
    }
  }

  /// One connection from negotiation to its end. True when the handshake
  /// went through, so the pauses start again from the shortest.
  Future<bool> _session() async {
    final token = await accessToken();
    if (token == null) {
      return false;
    }

    final connectionToken = await negotiate();
    if (!_running) {
      return false;
    }

    final socket = await connect(
      hubSocketUri(
        baseUrl,
        hubPath,
        connectionToken: connectionToken,
        accessToken: token,
      ),
    );
    _socket = socket;

    socket.send('{"protocol":"json","version":1}$recordSeparator');

    var handshakeDone = false;
    final ping = Timer.periodic(
      pingEvery,
      (_) => socket.send('{"type":6}$recordSeparator'),
    );
    Timer? silence;

    void watchSilence() {
      silence?.cancel();
      silence = Timer(serverTimeout, () => unawaited(socket.close()));
    }

    watchSilence();

    try {
      await for (final frame in socket.messages) {
        watchSilence();

        for (final message in parseHubFrame(frame)) {
          if (!handshakeDone) {
            if (message.containsKey('error')) {
              return false;
            }

            handshakeDone = true;
            _connected = true;

            if (_connectedOnce) {
              _reconnected.add(null);
            }

            _connectedOnce = true;
            continue;
          }

          switch (message['type']) {
            case 1:
              final target = message['target'];
              final arguments = message['arguments'];

              if (target is String &&
                  arguments is List &&
                  arguments.isNotEmpty &&
                  arguments.first is Map<String, dynamic>) {
                _events.add(
                  HubEvent(target, arguments.first as Map<String, dynamic>),
                );
              }
            case 7:
              // The server closed the connection; reconnect.
              await socket.close();
          }
        }
      }
    } finally {
      ping.cancel();
      silence?.cancel();
      _socket = null;
    }

    return handshakeDone;
  }

  Future<void> _pause(Duration delay) async {
    if (delay == Duration.zero) {
      return;
    }

    final wait = _wait = Completer<void>();
    final timer = Timer(delay, () {
      if (!wait.isCompleted) {
        wait.complete();
      }
    });

    await wait.future;
    timer.cancel();
  }
}

/// A [HubSocket] on a real WebSocket.
final class WebSocketHubSocket implements HubSocket {
  WebSocketHubSocket._(this._channel);

  final WebSocketChannel _channel;

  static Future<HubSocket> open(Uri uri) async {
    final channel = WebSocketChannel.connect(uri);
    await channel.ready;
    return WebSocketHubSocket._(channel);
  }

  @override
  Stream<String> get messages => _channel.stream.map(
    (message) =>
        message is String ? message : utf8.decode(message as List<int>),
  );

  @override
  void send(String text) => _channel.sink.add(text);

  @override
  Future<void> close() => _channel.sink.close();
}
