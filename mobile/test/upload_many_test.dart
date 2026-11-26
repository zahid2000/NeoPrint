import 'dart:async';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/features/files/file_models.dart';
import 'package:neoprint/features/files/files_controller.dart';
import 'package:neoprint/features/files/files_repository.dart';

/// Uploads that finish when the test says, counting how many run at once.
class SlowRepository implements FilesRepository {
  final waiting = <String, Completer<void>>{};
  final started = <String>[];
  var running = 0;
  var mostAtOnce = 0;

  @override
  Future<FilePage> list({int page = 1, int pageSize = 50}) async =>
      const FilePage(items: [], total: 0);

  @override
  Future<UserFile> upload({
    String? path,
    Uint8List? bytes,
    required String fileName,
    void Function(int sent, int total)? onProgress,
    CancelToken? cancelToken,
  }) async {
    started.add(fileName);
    running++;
    if (running > mostAtOnce) {
      mostAtOnce = running;
    }
    final done = waiting[fileName] = Completer<void>();
    try {
      await done.future;
    } finally {
      running--;
    }
    if (fileName.startsWith('bad')) {
      throw const ApiException(
        statusCode: 400,
        message: 'Bu format qəbul edilmir.',
      );
    }
    return UserFile.fromJson({
      'id': fileName,
      'fileName': fileName,
      'extension': extensionOf(fileName),
      'source': 'Uploaded',
      'status': 'Pending',
      'sizeBytes': 10,
      'pageCount': 0,
      'isSensitive': false,
      'createdAt': DateTime.utc(2026, 11, 26).toIso8601String(),
    });
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

PickedFile picked(String name) => PickedFile(
  name: name,
  path: '/files/$name',
  readBytes: () async => Uint8List(0),
);

void main() {
  test('several files upload three at a time, each with its own card, and '
      'the ones saved come back in order (T-464)', () async {
    final repository = SlowRepository();
    final container = ProviderContainer(
      overrides: [filesRepositoryProvider.overrideWithValue(repository)],
    );
    addTearDown(container.dispose);
    final controller = container.read(filesControllerProvider.notifier);

    final names = ['a.pdf', 'bad.exe', 'c.docx', 'd.jpg', 'e.txt'];
    final result = controller.uploadAll([
      for (final name in names) picked(name),
    ], parallel: 3);
    await pumpEventQueue();

    // Three on their way, the rest waiting their turn.
    expect(repository.started, ['a.pdf', 'bad.exe', 'c.docx']);
    expect(container.read(filesControllerProvider).uploads, hasLength(3));

    // Each one that finishes lets the next start.
    for (final name in names) {
      while (!repository.waiting.containsKey(name)) {
        await pumpEventQueue();
      }
      repository.waiting[name]!.complete();
      await pumpEventQueue();
    }

    final saved = await result;
    expect(saved.map((file) => file.id), ['a.pdf', 'c.docx', 'd.jpg', 'e.txt']);
    expect(repository.mostAtOnce, 3);

    // The one that was refused keeps its card, with the server's reason.
    final uploads = container.read(filesControllerProvider).uploads;
    expect(uploads.single.fileName, 'bad.exe');
    expect(uploads.single.error, isNotNull);
  });

  test('a single file is just one upload', () async {
    final repository = SlowRepository();
    final container = ProviderContainer(
      overrides: [filesRepositoryProvider.overrideWithValue(repository)],
    );
    addTearDown(container.dispose);

    final result = container.read(filesControllerProvider.notifier).uploadAll([
      picked('a.pdf'),
    ]);
    await pumpEventQueue();
    repository.waiting['a.pdf']!.complete();

    expect((await result).single.id, 'a.pdf');
    expect(repository.mostAtOnce, 1);
  });
}
