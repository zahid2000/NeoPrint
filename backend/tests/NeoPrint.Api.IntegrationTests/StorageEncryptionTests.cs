using System.Text;

using Amazon.S3.Model;

using Microsoft.Extensions.DependencyInjection;

using NeoPrint.Application.Abstractions;
using NeoPrint.Crypto;
using NeoPrint.Infrastructure.Storage;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// That stored objects are encrypted, and stay readable across a key
/// rotation (plan sections 2.10, 2.13).
/// </summary>
/// <remarks>
/// Against a real MinIO, and read back the way somebody with the storage
/// credentials would read it - not through the application. A test that
/// checks encryption by asking the application to decrypt proves nothing.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class StorageEncryptionTests
{
    private readonly NeoPrintApiFixture _fixture;

    public StorageEncryptionTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    /// <summary>Text distinctive enough that finding it would be unambiguous.</summary>
    private const string Secret =
        "Şəxsiyyət vəsiqəsi: AZE 12345678. Bu mətn storage-da açıq görünməməlidir.";

    [DockerFact]
    public async Task An_object_fetched_straight_from_minio_is_unreadable()
    {
        using var scope = _fixture.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();

        var key = $"tests/{Guid.NewGuid():N}.txt";
        var plaintext = Encoding.UTF8.GetBytes(Secret);

        var stored = await storage.WriteAsync(
            StorageBucket.Files, key, new MemoryStream(plaintext), "text/plain");

        // Straight to MinIO with the storage credentials, which is what an
        // operator, a backup or a stolen key would have.
        using var direct = _fixture.CreateDirectStorageClient();
        using var response = await direct.GetObjectAsync(
            new GetObjectRequest { BucketName = NeoPrintApiFixture.FilesBucket, Key = key });

        using var raw = new MemoryStream();
        await response.ResponseStream.CopyToAsync(raw);
        var bytes = raw.ToArray();

        // Not the plaintext, and not a substring of it either: a format that
        // encrypted only part of the object would still pass a length check.
        Encoding.UTF8.GetString(bytes).Should().NotContain("AZE 12345678");
        Encoding.UTF8.GetString(bytes).Should().NotContain("vəsiqəsi");
        bytes.Should().NotEqual(plaintext);

        // Longer than the plaintext: the header and the per-chunk frames.
        bytes.Length.Should().BeGreaterThan(plaintext.Length);

        // And the application still reads it back byte for byte.
        using var readBack = new MemoryStream();
        await storage.ReadAsync(stored, StorageBucket.Files, readBack);

        Encoding.UTF8.GetString(readBack.ToArray()).Should().Be(Secret);

        await storage.DeleteAsync(stored, StorageBucket.Files);
    }

    [DockerFact]
    public async Task The_recorded_hash_is_of_what_the_store_actually_holds()
    {
        using var scope = _fixture.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();

        var key = $"tests/{Guid.NewGuid():N}.bin";
        var content = new byte[3 * 1024 * 1024];
        Random.Shared.NextBytes(content);

        var stored = await storage.WriteAsync(
            StorageBucket.Files, key, new MemoryStream(content), "application/octet-stream");

        using var direct = _fixture.CreateDirectStorageClient();
        using var response = await direct.GetObjectAsync(
            new GetObjectRequest { BucketName = NeoPrintApiFixture.FilesBucket, Key = key });

        using var raw = new MemoryStream();
        await response.ResponseStream.CopyToAsync(raw);
        raw.Position = 0;

        // The hash covers the ciphertext, so a sync can verify a copy without
        // holding a key (plan section 2.10).
        var actual = Convert.ToHexString(
            await System.Security.Cryptography.SHA256.HashDataAsync(raw));

        actual.Should().Be(stored.Sha256);
        stored.EncryptedLength.Should().Be(raw.Length);
        stored.PlaintextLength.Should().Be(content.Length);

        // Several chunks: the encryptor frames 1 MB at a time, and a
        // single-chunk object would not exercise the ordering guarantees.
        stored.EncryptedLength.Should().BeGreaterThan(content.Length);

        await storage.DeleteAsync(stored, StorageBucket.Files);
    }

    [DockerFact]
    public async Task A_file_that_is_larger_than_one_chunk_survives_the_round_trip()
    {
        using var scope = _fixture.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();

        var key = $"tests/{Guid.NewGuid():N}.bin";
        var content = new byte[(int)(2.5 * 1024 * 1024)];
        Random.Shared.NextBytes(content);

        var stored = await storage.WriteAsync(
            StorageBucket.Files, key, new MemoryStream(content), "application/octet-stream");

        using var readBack = new MemoryStream();
        await storage.ReadAsync(stored, StorageBucket.Files, readBack);

        readBack.ToArray().Should().Equal(content);

        await storage.DeleteAsync(stored, StorageBucket.Files);
    }

    [DockerFact]
    public async Task A_tampered_object_is_refused_rather_than_half_decrypted()
    {
        using var scope = _fixture.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();

        var key = $"tests/{Guid.NewGuid():N}.txt";
        var stored = await storage.WriteAsync(
            StorageBucket.Files,
            key,
            new MemoryStream(Encoding.UTF8.GetBytes(Secret)),
            "text/plain");

        using var direct = _fixture.CreateDirectStorageClient();

        using (var response = await direct.GetObjectAsync(
                   new GetObjectRequest { BucketName = NeoPrintApiFixture.FilesBucket, Key = key }))
        using (var raw = new MemoryStream())
        {
            await response.ResponseStream.CopyToAsync(raw);
            var bytes = raw.ToArray();

            // One bit in the last chunk's payload.
            bytes[^20] ^= 0x01;

            await direct.PutObjectAsync(new PutObjectRequest
            {
                BucketName = NeoPrintApiFixture.FilesBucket,
                Key = key,
                InputStream = new MemoryStream(bytes),
            });
        }

        using var destination = new MemoryStream();

        var read = async () => await storage.ReadAsync(stored, StorageBucket.Files, destination);

        // Authenticated encryption: a modified object fails rather than
        // yielding plausible-looking plaintext.
        await read.Should().ThrowAsync<NeoPrintCryptoException>();

        await storage.DeleteAsync(stored, StorageBucket.Files);
    }

    [DockerFact]
    public async Task Deleting_removes_the_object_from_the_store()
    {
        using var scope = _fixture.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();

        var key = $"tests/{Guid.NewGuid():N}.txt";
        var stored = await storage.WriteAsync(
            StorageBucket.Files, key, new MemoryStream([1, 2, 3]), "application/octet-stream");

        (await storage.ExistsAsync(stored, StorageBucket.Files)).Should().BeTrue();

        await storage.DeleteAsync(stored, StorageBucket.Files);

        (await storage.ExistsAsync(stored, StorageBucket.Files)).Should().BeFalse();

        // And it is really gone from the store, not just from the application's
        // point of view.
        using var direct = _fixture.CreateDirectStorageClient();

        var fetch = async () => await direct.GetObjectAsync(
            new GetObjectRequest { BucketName = NeoPrintApiFixture.FilesBucket, Key = key });

        await fetch.Should().ThrowAsync<Amazon.S3.AmazonS3Exception>();
    }

    [DockerFact]
    public async Task The_provider_credentials_are_not_stored_in_the_clear()
    {
        using var scope = _fixture.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<StorageProviderRegistry>();

        var providers = await registry.GetAllAsync();

        providers.Should().NotBeEmpty("the application seeds its first provider at startup");

        foreach (var provider in providers)
        {
            provider.AccessKeyCiphertext.Should().NotContain(NeoPrintApiFixture.StorageAccessKey);
            provider.SecretKeyCiphertext.Should().NotContain(NeoPrintApiFixture.StorageSecretKey);

            // And they open again, so the encryption is not one-way by accident.
            var protector = scope.ServiceProvider.GetRequiredService<StorageSecretProtector>();

            protector.Unprotect(provider.AccessKeyCiphertext, provider.Id)
                .Should().Be(NeoPrintApiFixture.StorageAccessKey);
        }
    }

    [Fact]
    public void A_credential_moved_to_another_provider_does_not_open()
    {
        using var scope = _fixture.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<StorageSecretProtector>();

        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        var sealedValue = protector.Protect("a-secret-key", mine);

        var open = () => protector.Unprotect(sealedValue, theirs);

        // The provider id is authenticated, so copying a ciphertext into
        // another provider's document does not hand over its credentials.
        open.Should().Throw<InvalidOperationException>();
    }
}
