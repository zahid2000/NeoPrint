import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/realtime/user_hub.dart';

/// A WebSocket the test speaks for the server on.
final class FakeSocket implements HubSocket {
  final server = StreamController<String>();
  final sent = <String>[];
  bool closed = false;

  @override
  Stream<String> get messages => server.stream;

  @override
  void send(String text) => sent.add(text);

  @override
  Future<void> close() async {
    if (!closed) {
      closed = true;
      await server.close();
    }
  }
}

void main() {
  test('a frame splits into its messages and skips what is not JSON', () {
    final messages = parseHubFrame(
      '{}\u001e{"type":1,"target":"A","arguments":[{}]}\u001enot json\u001e',
    );

    expect(messages, hasLength(2));
    expect(messages.last['target'], 'A');
  });

  test('the socket address carries the tokens and the right scheme', () {
    final plain = hubSocketUri(
      'http://localhost:5080/',
      'hubs/user',
      connectionToken: 'c1',
      accessToken: 't1',
    );
    final secure = hubSocketUri(
      'https://api.neoprint.az/',
      'hubs/user',
      connectionToken: 'c1',
      accessToken: 't1',
    );

    expect(
      plain.toString(),
      'ws://localhost:5080/hubs/user?id=c1&access_token=t1',
    );
    expect(secure.scheme, 'wss');
    expect(secure.path, '/hubs/user');
  });

  test('after the handshake it passes on messages, pings and comes back after a drop', () async {
    final sockets = <FakeSocket>[];
    final opened = <Uri>[];

    final hub = UserHubConnection(
      baseUrl: 'http://localhost:5080/',
      negotiate: () async => 'conn-${sockets.length}',
      accessToken: () async => 'token',
      connect: (uri) async {
        opened.add(uri);
        final socket = FakeSocket();
        sockets.add(socket);
        return socket;
      },
      pingEvery: const Duration(milliseconds: 20),
      retryDelays: const [Duration(milliseconds: 1)],
    );

    final events = <HubEvent>[];
    var reconnections = 0;
    hub.events.listen(events.add);
    hub.reconnected.listen((_) => reconnections++);
    hub.start();

    await pumpUntil(() => sockets.isNotEmpty);
    final first = sockets.single;
    expect(first.sent.first, '{"protocol":"json","version":1}\u001e');

    // Nothing counts before the server accepts the handshake.
    first.server.add(
      '{"type":1,"target":"JobStatusChanged","arguments":[{"jobId":"j0"}]}\u001e',
    );
    await pumpUntil(() => true);
    expect(events, isEmpty);

    first.server.add('{}\u001e');
    first.server.add(
      '{"type":1,"target":"JobStatusChanged","arguments":[{"jobId":"j1","status":"Queued"}]}\u001e'
      '{"type":6}\u001e',
    );

    await pumpUntil(() => events.isNotEmpty);
    expect(events.single.target, 'JobStatusChanged');
    expect(events.single.payload['status'], 'Queued');
    expect(hub.isConnected, isTrue);

    await pumpUntil(() => first.sent.contains('{"type":6}\u001e'));

    // The server goes away; the hub negotiates again and says so.
    await first.close();
    await pumpUntil(() => sockets.length == 2);
    sockets.last.server.add('{}\u001e');
    await pumpUntil(() => reconnections == 1);
    expect(opened.last.queryParameters['id'], 'conn-1');

    await hub.dispose();
    expect(sockets.last.closed, isTrue);
  });

  test('a refused handshake is retried, not reported as connected', () async {
    final sockets = <FakeSocket>[];

    final hub = UserHubConnection(
      baseUrl: 'http://localhost:5080/',
      negotiate: () async => 'c',
      accessToken: () async => 'token',
      connect: (_) async {
        final socket = FakeSocket();
        sockets.add(socket);
        return socket;
      },
      retryDelays: const [Duration(milliseconds: 1)],
    )..start();

    await pumpUntil(() => sockets.isNotEmpty);
    sockets.first.server.add('{"error":"Unauthorized"}\u001e');

    await pumpUntil(() => sockets.length > 1);
    expect(hub.isConnected, isFalse);

    await hub.dispose();
  });

  test('signed out, it does not try to connect', () async {
    var connects = 0;

    final hub = UserHubConnection(
      baseUrl: 'http://localhost:5080/',
      negotiate: () async => 'c',
      accessToken: () async => null,
      connect: (_) async {
        connects++;
        return FakeSocket();
      },
      retryDelays: const [Duration(milliseconds: 1)],
    )..start();

    await Future<void>.delayed(const Duration(milliseconds: 30));
    expect(connects, 0);

    await hub.dispose();
  });
}

Future<void> pumpUntil(bool Function() done) async {
  final until = DateTime.now().add(const Duration(seconds: 5));

  do {
    await Future<void>.delayed(const Duration(milliseconds: 2));
  } while (!done() && DateTime.now().isBefore(until));

  expect(done(), isTrue, reason: 'timed out');
}
