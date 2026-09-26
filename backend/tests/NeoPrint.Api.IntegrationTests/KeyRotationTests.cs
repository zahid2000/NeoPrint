using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

using Amazon.S3.Model;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Application.Abstractions;
using NeoPrint.Crypto;
using NeoPrint.Infrastructure.Storage;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Rotating the key encryption key, and <c>KeyRewrapJob</c>
/// (plan section 2.13).
/// </summary>
/// <remarks>
/// <para>
/// The question these answer is the one that matters on the day of a
/// rotation: <em>can an object written with the old key still be read after
/// the new key is in place, and when is it safe to delete the old key?</em>
/// Getting it wrong makes every stored file unreadable, so it is tested
/// against a real object store rather than reasoned about.
/// </para>
/// <para>
/// The key ring is swapped here rather than through configuration, so the
/// test does not have to wait out the secret store's refresh interval. What
/// it swaps to is the same <see cref="KeyRing"/> the application builds.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class KeyRotationTests
{
    private readonly NeoPrintApiFixture _fixture;

    public KeyRotationTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private const string Content = "Rotasiyadan sonra da oxunmalıdır.";

    /// <summary>
    /// A key ring a test controls, standing in for the one the secret store
    /// builds.
    /// </summary>
    private sealed class SwappableKeys : IKeyEncryptionKeyProvider
    {
        private readonly Dictionary<string, KeyEncryptionKey> _keys = [];

        public SwappableKeys(params KeyEncryptionKey[] keys)
        {
            foreach (var key in keys)
            {
                _keys[key.Id] = key;
            }

            Current = keys[0];
        }

        public KeyEncryptionKey Current { get; private set; }

        /// <summary>Adds a key and makes it the one new objects use.</summary>
        public void Rotate(KeyEncryptionKey next)
        {
            _keys[next.Id] = next;
            Current = next;
        }

        /// <summary>Takes a retired key out of the ring, as deleting it would.</summary>
        public void Retire(string keyId) => _keys.Remove(keyId);

        public bool TryGet(string kekId, [NotNullWhen(true)] out KeyEncryptionKey? key)
            => _keys.TryGetValue(kekId, out key);
    }

    private static KeyEncryptionKey NewKey(string id)
        => KeyEncryptionKey.FromBase64(id, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    /// <summary>Storage wired to a key ring the test can rotate.</summary>
    private (S3FileStorage Storage, KeyRewrapJob Rewrap, SwappableKeys Keys) Build(SwappableKeys keys)
    {
        var scope = _fixture.Services.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<StorageProviderRegistry>();
        var clients = scope.ServiceProvider.GetRequiredService<S3ClientFactory>();
        var encryptor = new EnvelopeEncryptor(keys);

        return (
            new S3FileStorage(
                registry, clients, encryptor, keys, NullLogger<S3FileStorage>.Instance),
            new KeyRewrapJob(
                registry, clients, encryptor, keys, NullLogger<KeyRewrapJob>.Instance),
            keys);
    }

    [DockerFact]
    public async Task An_object_written_with_the_old_key_is_still_readable_after_a_rotation()
    {
        var first = NewKey("rotation-k1");
        var keys = new SwappableKeys(first);
        var (storage, _, _) = Build(keys);

        var key = $"tests/rotation/{Guid.NewGuid():N}.txt";
        var stored = await storage.WriteAsync(
            StorageBucket.Files, key, new MemoryStream(Encoding.UTF8.GetBytes(Content)), "text/plain");

        stored.KekId.Should().Be("rotation-k1");

        // The rotation: a new key becomes current, the old one stays in the
        // ring (plan section 2.13).
        keys.Rotate(NewKey("rotation-k2"));

        using var readBack = new MemoryStream();
        await storage.ReadAsync(stored, StorageBucket.Files, readBack);

        Encoding.UTF8.GetString(readBack.ToArray()).Should().Be(Content);

        await storage.DeleteAsync(stored, StorageBucket.Files);
    }

    [DockerFact]
    public async Task A_new_object_after_a_rotation_uses_the_new_key()
    {
        var keys = new SwappableKeys(NewKey("rotation-k3"));
        var (storage, _, _) = Build(keys);

        keys.Rotate(NewKey("rotation-k4"));

        var key = $"tests/rotation/{Guid.NewGuid():N}.txt";
        var stored = await storage.WriteAsync(
            StorageBucket.Files, key, new MemoryStream(Encoding.UTF8.GetBytes(Content)), "text/plain");

        stored.KekId.Should().Be("rotation-k4");

        await storage.DeleteAsync(stored, StorageBucket.Files);
    }

    [DockerFact]
    public async Task Rewrapping_moves_an_object_onto_the_new_key_and_lets_the_old_one_go()
    {
        var old = NewKey("rewrap-old");
        var keys = new SwappableKeys(old);
        var (storage, rewrap, _) = Build(keys);

        // A prefix of its own: the bucket is shared with every other test
        // in the collection, and objects written by the application use the
        // application's key ring, not this test's.
        var prefix = $"tests/rewrap-{Guid.NewGuid():N}/";
        var key = prefix + "document.txt";
        var stored = await storage.WriteAsync(
            StorageBucket.Files, key, new MemoryStream(Encoding.UTF8.GetBytes(Content)), "text/plain");

        stored.KekId.Should().Be("rewrap-old");

        keys.Rotate(NewKey("rewrap-new"));

        var summary = await rewrap.RunAsync(stored.ProviderId, StorageBucket.Files, prefix);

        summary.Failed.Should().Be(0);
        summary.Rewrapped.Should().BeGreaterThan(0);

        // The point of the whole exercise: once the pass has run, the old key
        // can be deleted from the secret store and nothing becomes
        // unreadable.
        keys.Retire("rewrap-old");

        using var readBack = new MemoryStream();
        await storage.ReadAsync(stored, StorageBucket.Files, readBack);

        Encoding.UTF8.GetString(readBack.ToArray()).Should().Be(Content);

        await storage.DeleteAsync(stored, StorageBucket.Files);
    }

    [DockerFact]
    public async Task Retiring_the_old_key_without_rewrapping_is_what_breaks_the_object()
    {
        // The negative of the test above. Without it, that one would pass even
        // if the rewrap did nothing at all.
        var keys = new SwappableKeys(NewKey("orphan-old"));
        var (storage, _, _) = Build(keys);

        var key = $"tests/rotation/{Guid.NewGuid():N}.txt";
        var stored = await storage.WriteAsync(
            StorageBucket.Files, key, new MemoryStream(Encoding.UTF8.GetBytes(Content)), "text/plain");

        keys.Rotate(NewKey("orphan-new"));
        keys.Retire("orphan-old");

        using var destination = new MemoryStream();
        var read = async () => await storage.ReadAsync(stored, StorageBucket.Files, destination);

        await read.Should().ThrowAsync<UnknownKeyException>(
            "an object whose key was deleted before the rewrap pass cannot be read");

        await storage.DeleteAsync(stored, StorageBucket.Files);
    }

    [DockerFact]
    public async Task Rewrapping_leaves_the_content_byte_for_byte_identical()
    {
        var keys = new SwappableKeys(NewKey("content-old"));
        var (storage, rewrap, _) = Build(keys);

        // Larger than one chunk: rewrapping must not touch the chunk framing.
        var content = new byte[(int)(2.5 * 1024 * 1024)];
        Random.Shared.NextBytes(content);

        var prefix = $"tests/rewrap-{Guid.NewGuid():N}/";
        var key = prefix + "large.bin";
        var stored = await storage.WriteAsync(
            StorageBucket.Files, key, new MemoryStream(content), "application/octet-stream");

        keys.Rotate(NewKey("content-new"));
        await rewrap.RunAsync(stored.ProviderId, StorageBucket.Files, prefix);
        keys.Retire("content-old");

        using var readBack = new MemoryStream();
        await storage.ReadAsync(stored, StorageBucket.Files, readBack);

        readBack.ToArray().Should().Equal(content);

        await storage.DeleteAsync(stored, StorageBucket.Files);
    }

    [DockerFact]
    public async Task Running_the_rewrap_twice_does_nothing_the_second_time()
    {
        var keys = new SwappableKeys(NewKey("twice-old"));
        var (storage, rewrap, _) = Build(keys);

        var prefix = $"tests/rewrap-{Guid.NewGuid():N}/";
        var key = prefix + "document.txt";
        var stored = await storage.WriteAsync(
            StorageBucket.Files, key, new MemoryStream(Encoding.UTF8.GetBytes(Content)), "text/plain");

        keys.Rotate(NewKey("twice-new"));

        var first = await rewrap.RunAsync(stored.ProviderId, StorageBucket.Files, prefix);
        var second = await rewrap.RunAsync(stored.ProviderId, StorageBucket.Files, prefix);

        first.Rewrapped.Should().BeGreaterThan(0);

        // Restart-safe: a pass that failed half way can simply be run again,
        // and what it already did is skipped.
        second.Rewrapped.Should().Be(0);
        second.AlreadyCurrent.Should().Be(second.Examined);

        await storage.DeleteAsync(stored, StorageBucket.Files);
    }

    [DockerFact]
    public async Task Rewrapping_does_not_change_what_the_store_holds_for_a_current_object()
    {
        var keys = new SwappableKeys(NewKey("stable-k1"));
        var (storage, rewrap, _) = Build(keys);

        var prefix = $"tests/rewrap-{Guid.NewGuid():N}/";
        var key = prefix + "stable.txt";
        var stored = await storage.WriteAsync(
            StorageBucket.Files, key, new MemoryStream(Encoding.UTF8.GetBytes(Content)), "text/plain");

        var before = await HashInStoreAsync(key);

        // No rotation: everything is already on the active key.
        var summary = await rewrap.RunAsync(stored.ProviderId, StorageBucket.Files, prefix);

        summary.Rewrapped.Should().Be(0);
        (await HashInStoreAsync(key)).Should().Be(before);

        await storage.DeleteAsync(stored, StorageBucket.Files);
    }

    private async Task<string> HashInStoreAsync(string key)
    {
        using var direct = _fixture.CreateDirectStorageClient();
        using var response = await direct.GetObjectAsync(
            new GetObjectRequest { BucketName = NeoPrintApiFixture.FilesBucket, Key = key });

        using var raw = new MemoryStream();
        await response.ResponseStream.CopyToAsync(raw);
        raw.Position = 0;

        return Convert.ToHexString(await SHA256.HashDataAsync(raw));
    }
}
