using System.Security.Cryptography;

using FluentAssertions;

using NeoPrint.Crypto;

namespace NeoPrint.Shared.Tests.Crypto;

/// <summary>
/// The kiosk camera's sealed segments (T-482): the backend's private key opens
/// them, nothing else does, and a changed segment is refused.
/// </summary>
public sealed class CameraSegmentCipherTests
{
    private static readonly RSA Backend = RSA.Create(3072);

    private static RSA PublicOnly(RSA key)
    {
        var copy = RSA.Create();
        copy.ImportSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo(), out _);
        return copy;
    }

    private static async Task<byte[]> SealAsync(byte[] plaintext, string keyId = "cam-1")
    {
        using var input = new MemoryStream(plaintext);
        using var output = new MemoryStream();
        using var publicKey = PublicOnly(Backend);
        await CameraSegmentCipher.EncryptAsync(input, output, keyId, publicKey);
        return output.ToArray();
    }

    private static async Task<byte[]> OpenAsync(byte[] sealedBytes, Func<string, RSA?> keys)
    {
        using var input = new MemoryStream(sealedBytes);
        using var output = new MemoryStream();
        await CameraSegmentCipher.DecryptAsync(input, output, keys);
        return output.ToArray();
    }

    [Fact]
    public async Task The_backend_key_opens_what_the_kiosk_sealed()
    {
        var video = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);

        var sealedBytes = await SealAsync(video);

        sealedBytes.AsSpan(0, 6).SequenceEqual("NPCAM1"u8).Should().BeTrue();
        (await OpenAsync(sealedBytes, id => id == "cam-1" ? Backend : null)).Should().Equal(video);
    }

    [Fact]
    public async Task The_header_names_the_key_in_the_clear()
    {
        var sealedBytes = await SealAsync([1, 2, 3], "cam-2026");

        using var input = new MemoryStream(sealedBytes);
        var header = await CameraSegmentCipher.ReadHeaderAsync(input);

        header.KeyId.Should().Be("cam-2026");
        header.WrappedKey.Should().HaveCount(384);
    }

    [Fact]
    public async Task Another_private_key_does_not_open_it()
    {
        var sealedBytes = await SealAsync([1, 2, 3]);
        using var other = RSA.Create(3072);

        var open = () => OpenAsync(sealedBytes, _ => other);

        await open.Should().ThrowAsync<CryptoAuthenticationException>();
    }

    [Fact]
    public async Task An_unknown_key_is_named()
    {
        var sealedBytes = await SealAsync([1, 2, 3], "cam-old");

        var open = () => OpenAsync(sealedBytes, _ => null);

        (await open.Should().ThrowAsync<UnknownKeyException>()).Which.KekId.Should().Be("cam-old");
    }

    [Fact]
    public async Task A_changed_byte_in_the_video_is_refused()
    {
        var sealedBytes = await SealAsync(RandomNumberGenerator.GetBytes(10_000));
        sealedBytes[^20] ^= 0x01;

        var open = () => OpenAsync(sealedBytes, _ => Backend);

        await open.Should().ThrowAsync<CryptoAuthenticationException>();
    }

    [Fact]
    public async Task A_file_cut_short_is_refused()
    {
        var sealedBytes = await SealAsync(RandomNumberGenerator.GetBytes(10_000));

        var open = () => OpenAsync(sealedBytes[..^100], _ => Backend);

        await open.Should().ThrowAsync<NeoPrintCryptoException>();
    }

    [Fact]
    public async Task Something_else_is_not_taken_for_a_segment()
    {
        var open = () => OpenAsync("not a camera file"u8.ToArray(), _ => Backend);

        await open.Should().ThrowAsync<CryptoFormatException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("with space")]
    [InlineData("../etc")]
    public async Task A_key_id_that_is_not_a_plain_name_is_refused(string keyId)
    {
        var seal = () => SealAsync([1], keyId);

        await seal.Should().ThrowAsync<ArgumentException>();
    }
}
