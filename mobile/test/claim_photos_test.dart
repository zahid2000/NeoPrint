import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:image/image.dart' as img;
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/features/jobs/claim_photos.dart';
import 'package:neoprint/features/jobs/jobs_controller.dart';
import 'package:neoprint/features/jobs/jobs_repository.dart';

/// A claim the test opens, and the pictures that reach it.
class FakeReports implements JobsRepository {
  FakeReports({this.refuseSecond = false});

  final bool refuseSecond;
  final uploaded = <String>[];

  @override
  Future<String> report(
    String jobId,
    String text, {
    required String idempotencyKey,
  }) async => 'claim-1';

  @override
  Future<void> addClaimPhoto(
    String claimId,
    Uint8List jpeg, {
    required String idempotencyKey,
  }) async {
    if (refuseSecond && uploaded.length == 1) {
      uploaded.add('refused');
      throw const ApiException(statusCode: 400, message: 'Bad');
    }
    uploaded.add(idempotencyKey);
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

ProviderContainer container(FakeReports reports) => ProviderContainer(
  overrides: [jobsRepositoryProvider.overrideWithValue(reports)],
);

void main() {
  test(
    'a large phone picture is made a JPEG of at most 1600 px (T-438)',
    () async {
      final big = img.Image(width: 4000, height: 3000);
      final jpeg = await shrinkClaimPhoto(img.encodePng(big));

      final back = img.decodeJpg(jpeg!)!;
      expect(back.width, claimPhotoMaxSide);
      expect(back.height, 1200);
      expect(await shrinkClaimPhoto(Uint8List.fromList([1, 2, 3])), isNull);
    },
  );

  test(
    'the claim goes first, then each picture with a key of its own',
    () async {
      final reports = FakeReports();
      final photos = [
        ClaimPhotoDraft(Uint8List.fromList([1])),
        ClaimPhotoDraft(Uint8List.fromList([2])),
      ];

      final result = await container(reports)
          .read(myJobsProvider.notifier)
          .report(
            'job-1',
            'Kağız sıxışdı',
            idempotencyKey: 'k',
            photos: photos,
          );

      expect(result.error, isNull);
      expect(result.photosFailed, 0);
      expect(reports.uploaded, [
        photos[0].idempotencyKey,
        photos[1].idempotencyKey,
      ]);
    },
  );

  test(
    'a picture that did not go is counted; the claim is sent all the same',
    () async {
      final reports = FakeReports(refuseSecond: true);

      final result = await container(reports)
          .read(myJobsProvider.notifier)
          .report(
            'job-1',
            'Kağız sıxışdı',
            idempotencyKey: 'k',
            photos: [
              ClaimPhotoDraft(Uint8List.fromList([1])),
              ClaimPhotoDraft(Uint8List.fromList([2])),
            ],
          );

      expect(result.error, isNull);
      expect(result.photosFailed, 1);
    },
  );
}
