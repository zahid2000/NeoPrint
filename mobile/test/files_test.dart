import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/format/formats.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/storage/secure_storage.dart';
import 'package:neoprint/core/time/app_clock.dart';
import 'package:neoprint/features/files/file_models.dart';
import 'package:neoprint/features/files/files_controller.dart';
import 'package:neoprint/features/files/files_repository.dart';
import 'package:neoprint/features/files/heic_converter.dart';

/// Answers requests from a list the test fills in, and keeps what was sent.
///
/// Written by hand: plan section 11 rules out a mocking library.
final class RecordingAdapter implements HttpClientAdapter {
  final List<RequestOptions> requests = [];
  final List<ResponseBody Function(RequestOptions)> _answers = [];

  void answer(ResponseBody Function(RequestOptions) respond) =>
      _answers.add(respond);

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(options);

    // Drain the body, as the network would.
    await requestStream?.drain<void>();

    return _answers.removeAt(0)(options);
  }

  @override
  void close({bool force = false}) {}
}

ResponseBody json(int status, Object body) => ResponseBody.fromString(
  jsonEncode(body),
  status,
  headers: {
    Headers.contentTypeHeader: [Headers.jsonContentType],
  },
);

final class MemoryStorage implements SecureStorage {
  final Map<String, String> values = {};

  @override
  Future<String?> read(String key) async => values[key];

  @override
  Future<void> write(String key, String value) async => values[key] = value;

  @override
  Future<void> delete(String key) async => values.remove(key);

  @override
  Future<void> clear() async => values.clear();
}

({FilesRepository repository, RecordingAdapter adapter}) repositoryWith() {
  final adapter = RecordingAdapter();
  final dio = Dio(
    BaseOptions(baseUrl: 'http://api.test/', validateStatus: (_) => true),
  )..httpClientAdapter = adapter;

  final client = ApiClient(
    baseUrl: 'http://api.test/',
    storage: MemoryStorage(),
    tokens: AccessTokenHolder()..set('token', DateTime.utc(2999)),
    dio: dio,
  );

  return (repository: FilesRepository(client), adapter: adapter);
}

Map<String, dynamic> fileJson({
  String id = 'f1',
  String status = 'Ready',
  int pageCount = 3,
}) => {
  'id': id,
  'fileName': 'hesabat.pdf',
  'extension': 'pdf',
  'mediaType': 'application/pdf',
  'source': 'Scan',
  'status': status,
  'sizeBytes': 2048,
  'pageCount': pageCount,
  'isSensitive': true,
  'autoDelete': 'Never',
  'versionCount': 0,
  'createdAt': '2026-09-27T10:00:00Z',
};

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('file models', () {
    test('read a file as the API sends it', () {
      final file = UserFile.fromJson(fileJson());

      expect(file.source, FileSourceKind.scan);
      expect(file.status, FileStatus.ready);
      expect(file.isSensitive, isTrue);
      expect(file.canPreview, isTrue);
    });

    test('treat missing optional fields as their defaults', () {
      // The API leaves nulls out altogether.
      final file = UserFile.fromJson({'id': 'x', 'status': 'Converting'});

      expect(file.failureReason, isNull);
      expect(file.pageCount, 0);
      expect(file.status.isSettled, isFalse);
      expect(file.canPreview, isFalse);
    });

    test('take the extension from the name, lower case', () {
      expect(extensionOf('IMG_0001.HEIC'), 'heic');
      expect(extensionOf('no-extension'), '');
      expect(extensionOf('trailing.'), '');
    });
  });

  group('formats', () {
    final now = DateTime(2026, 9, 27, 18, 0);

    test('write today and yesterday as words, older dates in full', () {
      String format(DateTime value) =>
          formatDateTime(value, today: 'Bu gün', yesterday: 'Dünən', now: now);

      expect(format(DateTime(2026, 9, 27, 9, 5)), 'Bu gün 09:05');
      expect(format(DateTime(2026, 9, 26, 23, 59)), 'Dünən 23:59');
      expect(format(DateTime(2026, 9, 7, 14, 20)), '07.09.2026 14:20');
    });

    test('read the day against the clock of the app when given no moment', () {
      String format(DateTime value) =>
          formatDateTime(value, today: 'Bu gün', yesterday: 'Dünən');

      // The suite's own day (flutter_test_config.dart), not the real one.
      expect(format(DateTime(2026, 11, 15, 8, 30)), 'Bu gün 08:30');
      expect(format(DateTime(2026, 11, 14, 8, 30)), 'Dünən 08:30');

      // Another day: the same dates are just dates. A test that showed a
      // date written into it against the real clock failed on that date.
      setAppClock(() => DateTime(2030, 3, 14, 12));

      expect(format(DateTime(2026, 11, 15, 8, 30)), '15.11.2026 08:30');
      expect(format(DateTime(2030, 3, 14, 9)), 'Bu gün 09:00');
      expect(format(DateTime(2030, 3, 13, 9)), 'Dünən 09:00');
    });

    test('write sizes with a decimal comma', () {
      expect(formatBytes(512), '512 B');
      expect(formatBytes(1536), '1,5 KB');
      expect(formatBytes(12 * 1024 * 1024), '12 MB');
      expect(formatBytes(1536, language: 'en'), '1.5\u00a0KB');
    });

    test('money follows the language', () {
      expect(formatMoney(1240), '1\u00a0240,00\u00a0₼');
      expect(formatMoney(12.5, language: 'ru'), '12,50\u00a0₼');
      expect(formatMoney(1240, language: 'en'), '1,240.00\u00a0₼');
      expect(formatMoney(-3), '−3,00\u00a0₼');
    });
  });

  group('HEIC conversion', () {
    const channel = MethodChannel(HeicConverter.channelName);
    final messenger =
        TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;

    tearDown(() => messenger.setMockMethodCallHandler(channel, null));

    test('hands back the JPEG the platform wrote', () async {
      messenger.setMockMethodCallHandler(channel, (call) async {
        expect(call.method, 'toJpeg');
        expect((call.arguments as Map)['path'], '/photos/a.heic');
        return '/cache/a.jpg';
      });

      expect(
        await const HeicConverter().toJpeg('/photos/a.heic'),
        '/cache/a.jpg',
      );
    });

    test('tells an old phone apart from a broken photo', () async {
      messenger.setMockMethodCallHandler(
        channel,
        (_) async => throw PlatformException(code: 'UNSUPPORTED'),
      );

      await expectLater(
        const HeicConverter().toJpeg('/photos/a.heic'),
        throwsA(
          isA<HeicConversionException>().having(
            (error) => error.reason,
            'reason',
            HeicFailure.unsupported,
          ),
        ),
      );

      messenger.setMockMethodCallHandler(
        channel,
        (_) async => throw PlatformException(code: 'FAILED'),
      );

      await expectLater(
        const HeicConverter().toJpeg('/photos/a.heic'),
        throwsA(
          isA<HeicConversionException>().having(
            (error) => error.reason,
            'reason',
            HeicFailure.failed,
          ),
        ),
      );
    });
  });

  group('files repository', () {
    test('lists the files', () async {
      final (:repository, :adapter) = repositoryWith();
      adapter.answer(
        (_) => json(200, {
          'items': [fileJson()],
          'total': 1,
        }),
      );

      final page = await repository.list();

      expect(page.total, 1);
      expect(page.items.single.fileName, 'hesabat.pdf');
      expect(adapter.requests.single.uri.path, '/api/files');
    });

    test('uploads the bytes as multipart, marked as an upload', () async {
      final (:repository, :adapter) = repositoryWith();
      adapter.answer(
        (_) => json(201, fileJson(status: 'Pending', pageCount: 0)),
      );

      final uploaded = await repository.upload(
        bytes: Uint8List.fromList(utf8.encode('%PDF-1.4')),
        fileName: 'hesabat.pdf',
      );

      final form = adapter.requests.single.data as FormData;

      expect(form.files.single.value.filename, 'hesabat.pdf');
      expect(
        form.fields.map((field) => '${field.key}=${field.value}'),
        contains('source=Uploaded'),
      );
      expect(uploaded.status, FileStatus.pending);
    });

    test(
      'asks for a page again with its tag, and keeps the copy on 304',
      () async {
        final (:repository, :adapter) = repositoryWith();
        final png = Uint8List.fromList([137, 80, 78, 71]);

        adapter.answer(
          (_) => ResponseBody.fromBytes(
            png,
            200,
            headers: {
              'etag': ['"abc-1-960"'],
              Headers.contentTypeHeader: ['image/png'],
            },
          ),
        );
        adapter.answer((_) => ResponseBody.fromBytes(const [], 304));

        expect(await repository.pageImage('f1', 1, 1080), png);
        expect(await repository.pageImage('f1', 1, 1080), png);

        expect(adapter.requests[1].headers['If-None-Match'], '"abc-1-960"');
        expect(adapter.requests[1].uri.queryParameters['width'], '1080');
      },
    );
  });

  group('files controller', () {
    test('converts a HEIC photo before uploading it as a JPEG', () async {
      final repository = FakeRepository();
      final container = ProviderContainer(
        overrides: [
          filesRepositoryProvider.overrideWithValue(repository),
          heicConverterProvider.overrideWithValue(FakeConverter()),
        ],
      );
      addTearDown(container.dispose);

      await container.read(filesControllerProvider.notifier).load();
      await container
          .read(filesControllerProvider.notifier)
          .upload(
            PickedFile(
              name: 'IMG_0001.HEIC',
              path: '/photos/IMG_0001.HEIC',
              readBytes: _never,
            ),
          );

      expect(repository.uploaded, [('/cache/converted.jpg', 'IMG_0001.jpg')]);
      expect(container.read(filesControllerProvider).uploads, isEmpty);
    });

    test(
      'explains a HEIC photo it cannot convert, without uploading',
      () async {
        final repository = FakeRepository();
        final container = ProviderContainer(
          overrides: [
            filesRepositoryProvider.overrideWithValue(repository),
            heicConverterProvider.overrideWithValue(FakeConverter()),
          ],
        );
        addTearDown(container.dispose);

        // The web build has no path, so the platform cannot convert it.
        await container
            .read(filesControllerProvider.notifier)
            .upload(PickedFile(name: 'a.heic', readBytes: _never));

        final upload = container.read(filesControllerProvider).uploads.single;

        expect(upload.error, isA<HeicUploadError>());
        expect(repository.uploaded, isEmpty);
      },
    );

    test('keeps the server’s reason when the upload is refused', () async {
      final repository = FakeRepository()
        ..refuse = const ApiException(
          statusCode: 400,
          message: 'Fayl 60 MB-dır; icazə verilən maksimum 50 MB.',
        );
      final container = ProviderContainer(
        overrides: [filesRepositoryProvider.overrideWithValue(repository)],
      );
      addTearDown(container.dispose);

      await container
          .read(filesControllerProvider.notifier)
          .upload(
            PickedFile(name: 'big.pdf', path: '/big.pdf', readBytes: _never),
          );

      final error = container
          .read(filesControllerProvider)
          .uploads
          .single
          .error;

      expect(error, isA<ServerUploadError>());
      expect((error! as ServerUploadError).error.message, contains('50 MB'));
    });
  });
}

Future<Uint8List> _never() => throw StateError('The bytes were not needed.');

class FakeRepository implements FilesRepository {
  final List<(String?, String)> uploaded = [];
  ApiException? refuse;

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
    if (refuse != null) {
      throw refuse!;
    }

    uploaded.add((path, fileName));
    onProgress?.call(1, 1);

    return UserFile.fromJson(fileJson());
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

class FakeConverter extends HeicConverter {
  FakeConverter();

  @override
  Future<String> toJpeg(String path) async => '/cache/converted.jpg';
}
