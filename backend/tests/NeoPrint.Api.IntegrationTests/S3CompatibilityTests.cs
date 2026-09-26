using System.Net;
using System.Security.Cryptography;

using Amazon.S3;
using Amazon.S3.Model;

using NeoPrint.Domain.Storage;
using NeoPrint.Infrastructure.Storage;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Every S3 operation NeoPrint relies on, against the pinned RustFS build
/// (docs/DECISIONS.md: RustFS as the development, test and pilot default).
/// </summary>
/// <remarks>
/// <para>
/// The client is the one production builds (<see cref="S3ClientFactory"/>):
/// path-style addressing, checksums only when required, the same timeouts.
/// A test that passed with a friendlier client would prove nothing about the
/// application.
/// </para>
/// <para>
/// Operations the application uses today - put, get, streamed get, head,
/// delete, paged list with <c>StartAfter</c>, bucket location - are here, and
/// so are multipart upload and server-side copy, which it does not use yet but
/// which a large file or a same-provider copy would need. If RustFS breaks one
/// of these, this class is where it shows, before a user's file does.
/// </para>
/// <para>
/// Each run works in a bucket of its own, so nothing here depends on or
/// disturbs what other tests store.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class S3CompatibilityTests : IAsyncLifetime
{
    private const int MiB = 1024 * 1024;

    private readonly NeoPrintApiFixture _fixture;
    private readonly string _bucket = $"s3-compat-{Guid.NewGuid():N}";
    private readonly string _otherBucket = $"s3-compat-copy-{Guid.NewGuid():N}"[..40];
    private AmazonS3Client _client = null!;

    public S3CompatibilityTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _client = Client(NeoPrintApiFixture.StorageAccessKey, NeoPrintApiFixture.StorageSecretKey);

        await _client.PutBucketAsync(new PutBucketRequest { BucketName = _bucket });
        await _client.PutBucketAsync(new PutBucketRequest { BucketName = _otherBucket });
    }

    public async Task DisposeAsync()
    {
        foreach (var bucket in new[] { _bucket, _otherBucket })
        {
            await EmptyAndDeleteAsync(bucket);
        }

        _client.Dispose();
    }

    private AmazonS3Client Client(string accessKey, string secretKey)
        => S3ClientFactory.ForUnsaved(_fixture.StorageEndpoint, "us-east-1", useSsl: false, accessKey, secretKey);

    // -----------------------------------------------------------------------
    // Put and get
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task Put_from_a_file_and_get_returns_the_same_bytes_and_metadata()
    {
        var payload = Payload(300 * 1024, seed: 1);
        var scratch = Path.GetTempFileName();

        try
        {
            await File.WriteAllBytesAsync(scratch, payload);

            // As S3FileStorage writes: a seekable file stream, a fixed content
            // type and the application's own metadata.
            await using (var body = File.OpenRead(scratch))
            {
                var put = new PutObjectRequest
                {
                    BucketName = _bucket,
                    Key = "files/2026/09/object.bin",
                    InputStream = body,
                    ContentType = "application/octet-stream",
                    AutoCloseStream = false,
                };
                put.Metadata.Add("neoprint-kek", "k1");
                put.Metadata.Add("neoprint-content-type", "application/pdf");

                await _client.PutObjectAsync(put);
            }

            using var response = await _client.GetObjectAsync(_bucket, "files/2026/09/object.bin");

            (await ReadAllAsync(response.ResponseStream)).Should().Equal(payload);
            response.Headers.ContentLength.Should().Be(payload.Length);
            response.Headers.ContentType.Should().Be("application/octet-stream");
            response.Metadata["neoprint-kek"].Should().Be("k1");
            response.Metadata["neoprint-content-type"].Should().Be("application/pdf");
        }
        finally
        {
            File.Delete(scratch);
        }
    }

    [DockerFact]
    public async Task Overwriting_a_key_replaces_the_object()
    {
        // KeyRewrapJob and a re-run migration both overwrite in place.
        await PutAsync("overwrite.bin", Payload(1024, seed: 2));
        await PutAsync("overwrite.bin", Payload(2048, seed: 3));

        using var response = await _client.GetObjectAsync(_bucket, "overwrite.bin");

        (await ReadAllAsync(response.ResponseStream)).Should().Equal(Payload(2048, seed: 3));
    }

    [DockerFact]
    public async Task An_empty_object_round_trips()
    {
        await PutAsync("empty.bin", []);

        using var response = await _client.GetObjectAsync(_bucket, "empty.bin");

        (await ReadAllAsync(response.ResponseStream)).Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Streaming
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_large_object_streams_back_in_pieces_with_the_same_hash()
    {
        var payload = Payload(12 * MiB, seed: 4);
        await PutAsync("large.bin", payload);

        using var response = await _client.GetObjectAsync(_bucket, "large.bin");

        // Read as S3FileStorage decrypts: a small buffer at a time, never the
        // whole object in memory.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;

        while ((read = await response.ResponseStream.ReadAsync(buffer)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            total += read;
        }

        total.Should().Be(payload.Length);
        hash.GetHashAndReset().Should().Equal(SHA256.HashData(payload));
    }

    [DockerFact]
    public async Task A_ranged_get_returns_exactly_the_requested_bytes()
    {
        var payload = Payload(MiB, seed: 5);
        await PutAsync("range.bin", payload);

        using var response = await _client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _bucket,
            Key = "range.bin",
            ByteRange = new ByteRange(1000, 1999),
        });

        response.HttpStatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await ReadAllAsync(response.ResponseStream)).Should().Equal(payload[1000..2000]);
    }

    [DockerFact]
    public async Task Put_from_a_forward_only_stream_stores_every_byte()
    {
        var payload = Payload(3 * MiB + 17, seed: 6);

        // No seeking, so the SDK cannot hash the body up front: it signs it
        // chunk by chunk as it streams (aws-chunked), which the store has to
        // understand.
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = "unseekable.bin",
            InputStream = new ForwardOnlyStream(payload),
            AutoCloseStream = true,
        };
        request.Headers.ContentLength = payload.Length;

        await _client.PutObjectAsync(request);

        using var response = await _client.GetObjectAsync(_bucket, "unseekable.bin");

        (await ReadAllAsync(response.ResponseStream)).Should().Equal(payload);
    }

    [Fact]
    public async Task A_put_without_a_length_is_refused_by_the_client_before_any_request()
    {
        // Why S3FileStorage spools to a temporary file before writing: a
        // single PUT needs its length, and the SDK refuses one without it
        // in-process. This is the SDK's contract, not the store's - the
        // endpoint below does not exist and is never contacted.
        using var client = S3ClientFactory.ForUnsaved(
            "http://127.0.0.1:9", "us-east-1", useSsl: false, "key", "secret");

        var failure = await FluentActions
            .Awaiting(() => client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = "any",
                Key = "unknown-length.bin",
                InputStream = new ForwardOnlyStream(Payload(1024, seed: 6)),
            }))
            .Should().ThrowAsync<AmazonS3Exception>();

        failure.Which.Message.Should().Contain("content length");
    }

    // -----------------------------------------------------------------------
    // Multipart
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_multipart_upload_assembles_the_parts_in_order()
    {
        var payload = Payload(12 * MiB + 123, seed: 7);
        const string key = "multipart/assembled.bin";

        var upload = await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = _bucket,
            Key = key,
            ContentType = "application/octet-stream",
        });

        var parts = new List<PartETag>();
        var partSize = 5 * MiB;

        for (int number = 1, offset = 0; offset < payload.Length; number++, offset += partSize)
        {
            var slice = payload[offset..Math.Min(offset + partSize, payload.Length)];

            var part = await _client.UploadPartAsync(new UploadPartRequest
            {
                BucketName = _bucket,
                Key = key,
                UploadId = upload.UploadId,
                PartNumber = number,
                InputStream = new MemoryStream(slice),
                PartSize = slice.Length,
            });

            parts.Add(new PartETag(number, part.ETag));
        }

        parts.Should().HaveCount(3);

        var listed = await _client.ListPartsAsync(new ListPartsRequest
        {
            BucketName = _bucket,
            Key = key,
            UploadId = upload.UploadId,
        });

        listed.Parts.Select(p => p.PartNumber).Should().Equal(1, 2, 3);

        var completed = await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = _bucket,
            Key = key,
            UploadId = upload.UploadId,
            PartETags = parts,
        });

        // A multipart ETag is the hash of the part hashes, suffixed by the
        // part count - not the object's MD5.
        completed.ETag.Trim('"').Should().EndWith("-3");

        using var response = await _client.GetObjectAsync(_bucket, key);

        (await ReadAllAsync(response.ResponseStream)).Should().Equal(payload);
    }

    [DockerFact]
    public async Task An_aborted_multipart_upload_leaves_nothing_behind()
    {
        const string key = "multipart/aborted.bin";

        var upload = await _client.InitiateMultipartUploadAsync(_bucket, key);

        await _client.UploadPartAsync(new UploadPartRequest
        {
            BucketName = _bucket,
            Key = key,
            UploadId = upload.UploadId,
            PartNumber = 1,
            InputStream = new MemoryStream(Payload(5 * MiB, seed: 8)),
        });

        await _client.AbortMultipartUploadAsync(_bucket, key, upload.UploadId);

        var pending = await _client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest
        {
            BucketName = _bucket,
            Prefix = key,
        });

        (pending.MultipartUploads ?? []).Should().BeEmpty();
        (await StatusOfHeadAsync(key)).Should().Be(HttpStatusCode.NotFound);
    }

    // -----------------------------------------------------------------------
    // Head and delete
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task Head_reports_length_and_metadata_without_the_body()
    {
        var payload = Payload(4096, seed: 9);
        await PutAsync("head.bin", payload, ("neoprint-kek", "k2"));

        var head = await _client.GetObjectMetadataAsync(_bucket, "head.bin");

        head.Headers.ContentLength.Should().Be(payload.Length);
        head.ETag.Should().NotBeNullOrWhiteSpace();
        head.Metadata["neoprint-kek"].Should().Be("k2");
    }

    [DockerFact]
    public async Task A_missing_object_is_reported_as_missing_not_as_a_failure()
    {
        // S3FileStorage tells "no such object" from "store unavailable" by
        // these: NoSuchKey on a GET, a bare 404 on a HEAD. A store that
        // answered anything else would trigger a failover instead.
        var get = await FluentActions
            .Awaiting(() => _client.GetObjectAsync(_bucket, "never-written.bin"))
            .Should().ThrowAsync<AmazonS3Exception>();

        get.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
        get.Which.ErrorCode.Should().Be("NoSuchKey");

        (await StatusOfHeadAsync("never-written.bin")).Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task Delete_removes_the_object_and_is_safe_to_repeat()
    {
        await PutAsync("delete.bin", Payload(512, seed: 10));

        await _client.DeleteObjectAsync(_bucket, "delete.bin");

        (await StatusOfHeadAsync("delete.bin")).Should().Be(HttpStatusCode.NotFound);

        // S3FileStorage deletes from every provider, including ones that never
        // had the object: a second delete must not throw.
        await FluentActions.Awaiting(() => _client.DeleteObjectAsync(_bucket, "delete.bin"))
            .Should().NotThrowAsync();
    }

    // -----------------------------------------------------------------------
    // List
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task List_pages_through_every_key_in_order_with_sizes()
    {
        var keys = Enumerable.Range(0, 7).Select(i => $"list/item-{i:D2}.bin").ToList();

        foreach (var (key, index) in keys.Select((k, i) => (k, i)))
        {
            await PutAsync(key, Payload(100 + index, seed: 20 + index));
        }

        await PutAsync("elsewhere/outside.bin", Payload(10, seed: 30));

        var seen = new List<S3Object>();
        string? continuation = null;
        var pages = 0;

        do
        {
            var page = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = "list/",
                MaxKeys = 3,
                ContinuationToken = continuation,
            });

            seen.AddRange(page.S3Objects ?? []);
            continuation = page.IsTruncated == true ? page.NextContinuationToken : null;
            pages++;
        }
        while (continuation is not null);

        pages.Should().Be(3);
        seen.Select(o => o.Key).Should().Equal(keys);
        seen.Select(o => o.Size).Should().Equal(Enumerable.Range(0, 7).Select(i => (long?)(100 + i)));
    }

    [DockerFact]
    public async Task List_resumes_after_a_checkpoint_key()
    {
        // StorageMigrationWorker restarts from its checkpoint with StartAfter.
        var keys = Enumerable.Range(0, 5).Select(i => $"resume/k{i}.bin").ToList();

        foreach (var key in keys)
        {
            await PutAsync(key, Payload(16, seed: 40));
        }

        var page = await _client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _bucket,
            Prefix = "resume/",
            StartAfter = "resume/k2.bin",
        });

        page.S3Objects.Select(o => o.Key).Should().Equal("resume/k3.bin", "resume/k4.bin");
    }

    [DockerFact]
    public async Task Listing_an_empty_prefix_returns_nothing_rather_than_failing()
    {
        var page = await _client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _bucket,
            Prefix = "nothing-here/",
        });

        (page.S3Objects ?? []).Should().BeEmpty();
        page.IsTruncated.Should().NotBe(true);
    }

    // -----------------------------------------------------------------------
    // Copy
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task Copy_within_and_across_buckets_keeps_bytes_and_metadata()
    {
        var payload = Payload(256 * 1024, seed: 50);
        await PutAsync("copy/source.bin", payload, ("neoprint-kek", "k1"));

        await _client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = "copy/source.bin",
            DestinationBucket = _bucket,
            DestinationKey = "copy/same-bucket.bin",
        });

        await _client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = "copy/source.bin",
            DestinationBucket = _otherBucket,
            DestinationKey = "copy/other-bucket.bin",
        });

        foreach (var (bucket, key) in new[] { (_bucket, "copy/same-bucket.bin"), (_otherBucket, "copy/other-bucket.bin") })
        {
            using var response = await _client.GetObjectAsync(bucket, key);

            (await ReadAllAsync(response.ResponseStream)).Should().Equal(payload);
            response.Metadata["neoprint-kek"].Should().Be("k1");
        }
    }

    [DockerFact]
    public async Task Copy_with_replaced_metadata_changes_only_the_metadata()
    {
        var payload = Payload(2048, seed: 51);
        await PutAsync("copy/replace.bin", payload, ("neoprint-kek", "k1"));

        var copy = new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = "copy/replace.bin",
            DestinationBucket = _bucket,
            DestinationKey = "copy/replace.bin",
            MetadataDirective = S3MetadataDirective.REPLACE,
            ContentType = "application/octet-stream",
        };
        copy.Metadata.Add("neoprint-kek", "k2");

        await _client.CopyObjectAsync(copy);

        using var response = await _client.GetObjectAsync(_bucket, "copy/replace.bin");

        (await ReadAllAsync(response.ResponseStream)).Should().Equal(payload);
        response.Metadata["neoprint-kek"].Should().Be("k2");
    }

    // -----------------------------------------------------------------------
    // Buckets, credentials and health
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task Bucket_location_answers_for_the_health_check()
    {
        var location = await _client.GetBucketLocationAsync(_bucket);

        location.HttpStatusCode.Should().Be(HttpStatusCode.OK);
    }

    [DockerFact]
    public async Task A_missing_bucket_is_named_as_such()
    {
        // Migration verification treats NoSuchBucket on the target as empty;
        // the connection test turns it into "Bucket mövcud deyil".
        var failure = await FluentActions
            .Awaiting(() => _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "no-such-bucket-here" }))
            .Should().ThrowAsync<AmazonS3Exception>();

        failure.Which.ErrorCode.Should().Be("NoSuchBucket");
    }

    [DockerFact]
    public async Task Wrong_credentials_are_told_apart()
    {
        // The connection test maps these two codes to different advice.
        using var wrongSecret = Client(NeoPrintApiFixture.StorageAccessKey, "not-the-secret-key");
        using var unknownKey = Client("unknown-access-key", NeoPrintApiFixture.StorageSecretKey);

        var badSecret = await FluentActions.Awaiting(() => wrongSecret.GetBucketLocationAsync(_bucket))
            .Should().ThrowAsync<AmazonS3Exception>();
        var badKey = await FluentActions.Awaiting(() => unknownKey.GetBucketLocationAsync(_bucket))
            .Should().ThrowAsync<AmazonS3Exception>();

        badSecret.Which.ErrorCode.Should().Be("SignatureDoesNotMatch");
        badKey.Which.ErrorCode.Should().Be("InvalidAccessKeyId");
    }

    [DockerFact]
    public async Task The_readiness_endpoint_the_adapter_uses_answers()
    {
        var provider = new StorageProvider
        {
            Type = StorageProviderType.RustFS,
            Endpoint = _fixture.StorageEndpoint,
        };

        var uri = StorageProviderProfile.ReadinessUri(provider);
        uri.Should().NotBeNull();

        using var http = new HttpClient();
        using var response = await http.GetAsync(uri);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "it needs no credentials, so it can be asked first");
    }

    [Fact]
    public void Only_products_with_a_known_readiness_endpoint_get_one()
    {
        StorageProviderProfile.ReadinessPath(StorageProviderType.RustFS).Should().Be("/health/ready");
        StorageProviderProfile.ReadinessPath(StorageProviderType.MinIO).Should().Be("/minio/health/ready");
        StorageProviderProfile.ReadinessPath(StorageProviderType.Scality).Should().BeNull();

        StorageProviderProfile.ReadinessUri(new StorageProvider
        {
            Type = StorageProviderType.RustFS,
            Endpoint = "http://rustfs:9000",
        })!.ToString().Should().Be("http://rustfs:9000/health/ready");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static byte[] Payload(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private async Task PutAsync(string key, byte[] payload, params (string Name, string Value)[] metadata)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = new MemoryStream(payload),
            ContentType = "application/octet-stream",
        };

        foreach (var (name, value) in metadata)
        {
            request.Metadata.Add(name, value);
        }

        await _client.PutObjectAsync(request);
    }

    private async Task<HttpStatusCode> StatusOfHeadAsync(string key)
    {
        try
        {
            await _client.GetObjectMetadataAsync(_bucket, key);
            return HttpStatusCode.OK;
        }
        catch (AmazonS3Exception exception)
        {
            return exception.StatusCode;
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private async Task EmptyAndDeleteAsync(string bucket)
    {
        try
        {
            var uploads = await _client.ListMultipartUploadsAsync(bucket);

            foreach (var upload in uploads.MultipartUploads ?? [])
            {
                await _client.AbortMultipartUploadAsync(bucket, upload.Key, upload.UploadId);
            }

            string? continuation = null;

            do
            {
                var page = await _client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = bucket,
                    ContinuationToken = continuation,
                });

                foreach (var item in page.S3Objects ?? [])
                {
                    await _client.DeleteObjectAsync(bucket, item.Key);
                }

                continuation = page.IsTruncated == true ? page.NextContinuationToken : null;
            }
            while (continuation is not null);

            await _client.DeleteBucketAsync(bucket);
        }
        catch (AmazonS3Exception)
        {
            // Clean-up only: the container is thrown away with the run.
        }
    }

    /// <summary>A stream that can only be read forwards and has no length.</summary>
    private sealed class ForwardOnlyStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
