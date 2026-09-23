using System.Security.Cryptography;

using NeoPrint.Crypto;

namespace NeoPrint.Shared.Tests.Crypto;

/// <summary>
/// Envelope encryption (plan sections 2.13 and 11): round trip, detection of
/// reordered or tampered chunks, key rotation, and failure with the wrong key.
/// </summary>
public sealed class EnvelopeEncryptorTests
{
    // Small enough that a few kilobytes of input span many chunks, which is
    // what makes the chunk-level tests meaningful.
    private const int TestChunkSize = 64;

    private static KeyEncryptionKey Key(string id) => new(id, RandomNumberGenerator.GetBytes(32));

    private static (EnvelopeEncryptor Encryptor, KeyRing Ring) Build(int chunkSize = TestChunkSize, params string[] keyIds)
    {
        var ids = keyIds.Length == 0 ? ["k1"] : keyIds;
        var ring = new KeyRing(ids.Select(Key).ToArray(), ids[0]);
        return (new EnvelopeEncryptor(ring, chunkSize), ring);
    }

    private static async Task<byte[]> EncryptAsync(EnvelopeEncryptor encryptor, byte[] plaintext)
    {
        using var input = new MemoryStream(plaintext);
        using var output = new MemoryStream();
        await encryptor.EncryptAsync(input, output);
        return output.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(EnvelopeEncryptor encryptor, byte[] ciphertext)
    {
        using var input = new MemoryStream(ciphertext);
        using var output = new MemoryStream();
        await encryptor.DecryptAsync(input, output);
        return output.ToArray();
    }

    // -----------------------------------------------------------------------
    // Round trip
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0)]            // empty file
    [InlineData(1)]
    [InlineData(TestChunkSize - 1)]
    [InlineData(TestChunkSize)]      // exactly one chunk
    [InlineData(TestChunkSize + 1)]  // spills into a second chunk
    [InlineData(TestChunkSize * 4)]  // exact multiple of the chunk size
    [InlineData(TestChunkSize * 7 + 13)]
    public async Task Round_trip_returns_the_original_bytes(int length)
    {
        var (encryptor, _) = Build();
        var plaintext = RandomNumberGenerator.GetBytes(length);

        var ciphertext = await EncryptAsync(encryptor, plaintext);
        var roundTripped = await DecryptAsync(encryptor, ciphertext);

        roundTripped.Should().Equal(plaintext);
    }

    [Fact]
    public async Task Round_trip_works_at_the_production_chunk_size()
    {
        var (encryptor, _) = Build(EnvelopeEncryptor.DefaultChunkSize);
        // Just over two 1 MB chunks, so the multi-chunk path runs for real.
        var plaintext = RandomNumberGenerator.GetBytes((EnvelopeEncryptor.DefaultChunkSize * 2) + 4096);

        var roundTripped = await DecryptAsync(encryptor, await EncryptAsync(encryptor, plaintext));

        roundTripped.Should().Equal(plaintext);
    }

    [Fact]
    public async Task Ciphertext_does_not_contain_the_plaintext()
    {
        var (encryptor, _) = Build();
        var marker = "SECRET-DOCUMENT-CONTENT"u8.ToArray();
        var plaintext = new byte[512];
        marker.CopyTo(plaintext, 100);

        var ciphertext = await EncryptAsync(encryptor, plaintext);

        IndexOf(ciphertext, marker).Should().Be(-1,
            "an object downloaded straight from MinIO must not be readable (plan section 2.13)");
    }

    [Fact]
    public async Task Encrypting_the_same_content_twice_produces_different_ciphertext()
    {
        var (encryptor, _) = Build();
        var plaintext = RandomNumberGenerator.GetBytes(256);

        var first = await EncryptAsync(encryptor, plaintext);
        var second = await EncryptAsync(encryptor, plaintext);

        // Fresh data key, object id and nonces every time.
        first.Should().NotEqual(second);
    }

    [Fact]
    public async Task Header_records_the_key_and_algorithm_for_the_file_metadata()
    {
        var (encryptor, ring) = Build();

        using var input = new MemoryStream(RandomNumberGenerator.GetBytes(10));
        using var output = new MemoryStream();
        var header = await encryptor.EncryptAsync(input, output);

        header.KekId.Should().Be(ring.Current.Id);
        header.ChunkSize.Should().Be(TestChunkSize);
        header.PlaintextLength.Should().Be(10);
        EncryptedObjectHeader.AlgorithmName.Should().Be("AES-256-GCM");
    }

    [Fact]
    public async Task Works_on_a_stream_whose_length_is_not_known_in_advance()
    {
        var (encryptor, _) = Build();
        var plaintext = RandomNumberGenerator.GetBytes(TestChunkSize * 3 + 7);

        using var input = new NonSeekableStream(plaintext);
        using var output = new MemoryStream();
        var header = await encryptor.EncryptAsync(input, output);

        header.PlaintextLength.Should().Be(-1, "the length could not be measured up front");
        (await DecryptAsync(encryptor, output.ToArray())).Should().Equal(plaintext);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(TestChunkSize)]
    [InlineData(TestChunkSize + 1)]
    [InlineData(TestChunkSize * 3)]
    public async Task Predicted_ciphertext_length_matches_what_is_actually_written(int length)
    {
        var (encryptor, ring) = Build();

        var predicted = encryptor.GetCiphertextLength(length, ring.Current.Id);
        var actual = (await EncryptAsync(encryptor, new byte[length])).Length;

        actual.Should().Be((int)predicted);
    }

    // -----------------------------------------------------------------------
    // Tampering
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Flipping_a_byte_of_the_ciphertext_is_detected()
    {
        var (encryptor, _) = Build();
        var ciphertext = await EncryptAsync(encryptor, RandomNumberGenerator.GetBytes(TestChunkSize * 2));

        ciphertext[^20] ^= 0xFF;

        await Assert.ThrowsAsync<CryptoAuthenticationException>(() => DecryptAsync(encryptor, ciphertext));
    }

    [Fact]
    public async Task Swapping_two_chunks_is_detected()
    {
        var (encryptor, _) = Build();
        // Three full chunks plus a short final one.
        var plaintext = RandomNumberGenerator.GetBytes(TestChunkSize * 3 + 10);
        var ciphertext = await EncryptAsync(encryptor, plaintext);

        var headerLength = await HeaderLengthOf(ciphertext);
        var frameLength = TestChunkSize + FrameOverhead;

        // Chunks 0 and 1 are both full, so swapping them keeps every length and
        // offset intact. Only the index bound into the additional data differs.
        var tampered = (byte[])ciphertext.Clone();
        Array.Copy(ciphertext, headerLength + frameLength, tampered, headerLength, frameLength);
        Array.Copy(ciphertext, headerLength, tampered, headerLength + frameLength, frameLength);

        var exception = await Assert.ThrowsAsync<CryptoAuthenticationException>(
            () => DecryptAsync(encryptor, tampered));
        exception.Message.Should().Contain("reordered");
    }

    [Fact]
    public async Task Duplicating_a_chunk_is_detected()
    {
        var (encryptor, _) = Build();
        var ciphertext = await EncryptAsync(encryptor, RandomNumberGenerator.GetBytes(TestChunkSize * 3 + 10));

        var headerLength = await HeaderLengthOf(ciphertext);
        var frameLength = TestChunkSize + FrameOverhead;

        // Repeat chunk 0 in place of chunk 1.
        var tampered = (byte[])ciphertext.Clone();
        Array.Copy(ciphertext, headerLength, tampered, headerLength + frameLength, frameLength);

        await Assert.ThrowsAsync<CryptoAuthenticationException>(() => DecryptAsync(encryptor, tampered));
    }

    [Fact]
    public async Task Truncating_the_object_after_a_whole_chunk_is_detected()
    {
        var (encryptor, _) = Build();
        var ciphertext = await EncryptAsync(encryptor, RandomNumberGenerator.GetBytes(TestChunkSize * 3 + 10));

        var headerLength = await HeaderLengthOf(ciphertext);
        var frameLength = TestChunkSize + FrameOverhead;

        // Keep the header and the first two chunks, drop the rest. Without the
        // final-chunk flag in the additional data this would look like a
        // perfectly valid, shorter document.
        var truncated = ciphertext.AsSpan(0, headerLength + (frameLength * 2)).ToArray();

        var act = () => DecryptAsync(encryptor, truncated);
        await act.Should().ThrowAsync<NeoPrintCryptoException>();
    }

    [Fact]
    public async Task Tampering_with_the_header_is_detected()
    {
        var (encryptor, _) = Build();
        var ciphertext = await EncryptAsync(encryptor, RandomNumberGenerator.GetBytes(100));

        // Byte 6 is the first byte of the object id, which the data key
        // wrapping is authenticated against.
        ciphertext[6] ^= 0xFF;

        await Assert.ThrowsAsync<CryptoAuthenticationException>(() => DecryptAsync(encryptor, ciphertext));
    }

    [Fact]
    public async Task Data_that_is_not_an_encrypted_object_is_rejected_clearly()
    {
        var (encryptor, _) = Build();

        var exception = await Assert.ThrowsAsync<CryptoFormatException>(
            () => DecryptAsync(encryptor, "%PDF-1.7 this is a plain PDF, not an encrypted object"u8.ToArray()));

        exception.Message.Should().Contain("not an encrypted object");
    }

    [Fact]
    public async Task A_header_that_is_cut_short_is_rejected_as_malformed_not_as_tampering()
    {
        var (encryptor, _) = Build();
        var ciphertext = await EncryptAsync(encryptor, RandomNumberGenerator.GetBytes(100));

        await Assert.ThrowsAsync<CryptoFormatException>(
            () => DecryptAsync(encryptor, ciphertext.AsSpan(0, 20).ToArray()));
    }

    // -----------------------------------------------------------------------
    // Keys and rotation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Decrypting_with_a_different_key_fails()
    {
        var (writer, _) = Build();
        var ciphertext = await EncryptAsync(writer, RandomNumberGenerator.GetBytes(200));

        // Same key id, different key material: a restored backup pointed at the
        // wrong secret store, for example.
        var otherRing = new KeyRing([Key("k1")], "k1");
        var reader = new EnvelopeEncryptor(otherRing, TestChunkSize);

        await Assert.ThrowsAsync<CryptoAuthenticationException>(() => DecryptAsync(reader, ciphertext));
    }

    [Fact]
    public async Task An_object_wrapped_with_a_removed_key_reports_which_key_is_missing()
    {
        var oldKey = Key("k1");
        var writer = new EnvelopeEncryptor(new KeyRing([oldKey], "k1"), TestChunkSize);
        var ciphertext = await EncryptAsync(writer, RandomNumberGenerator.GetBytes(200));

        // k1 was dropped from the secret store before every object had been rewrapped.
        var reader = new EnvelopeEncryptor(new KeyRing([Key("k2")], "k2"), TestChunkSize);

        var exception = await Assert.ThrowsAsync<UnknownKeyException>(() => DecryptAsync(reader, ciphertext));
        exception.KekId.Should().Be("k1");
    }

    [Fact]
    public async Task A_retired_key_still_decrypts_objects_written_before_the_rotation()
    {
        var oldKey = Key("k1");
        var newKey = Key("k2");
        var plaintext = RandomNumberGenerator.GetBytes(TestChunkSize * 2 + 5);

        var beforeRotation = new EnvelopeEncryptor(new KeyRing([oldKey], "k1"), TestChunkSize);
        var ciphertext = await EncryptAsync(beforeRotation, plaintext);

        // After rotation both keys are present and k2 is current.
        var afterRotation = new EnvelopeEncryptor(new KeyRing([oldKey, newKey], "k2"), TestChunkSize);

        (await DecryptAsync(afterRotation, ciphertext)).Should().Equal(plaintext);
    }

    [Fact]
    public async Task Rewrapping_switches_the_key_without_re_encrypting_the_content()
    {
        var oldKey = Key("k1");
        var newKey = Key("k2");
        var plaintext = RandomNumberGenerator.GetBytes(TestChunkSize * 3 + 9);

        var beforeRotation = new EnvelopeEncryptor(new KeyRing([oldKey], "k1"), TestChunkSize);
        var original = await EncryptAsync(beforeRotation, plaintext);
        var originalHeaderLength = await HeaderLengthOf(original);

        var afterRotation = new EnvelopeEncryptor(new KeyRing([oldKey, newKey], "k2"), TestChunkSize);

        using var source = new MemoryStream(original);
        using var destination = new MemoryStream();
        var header = await afterRotation.RewrapAsync(source, destination);
        var rewrapped = destination.ToArray();

        header.Should().NotBeNull();
        header!.KekId.Should().Be("k2");

        // The encrypted content is byte-for-byte the same: only the header changed.
        rewrapped.AsSpan(originalHeaderLength).ToArray()
            .Should().Equal(original.AsSpan(originalHeaderLength).ToArray());

        (await DecryptAsync(afterRotation, rewrapped)).Should().Equal(plaintext);
    }

    [Fact]
    public async Task Rewrapping_an_object_that_is_already_current_changes_nothing()
    {
        var (encryptor, _) = Build();
        var original = await EncryptAsync(encryptor, RandomNumberGenerator.GetBytes(200));

        using var source = new MemoryStream(original);
        using var destination = new MemoryStream();
        var header = await encryptor.RewrapAsync(source, destination);

        header.Should().BeNull("the object was already wrapped with the active key");
        destination.ToArray().Should().Equal(original);
    }

    [Fact]
    public async Task A_rewrapped_object_can_no_longer_be_read_with_the_old_key_alone()
    {
        var oldKey = Key("k1");
        var newKey = Key("k2");

        var beforeRotation = new EnvelopeEncryptor(new KeyRing([oldKey], "k1"), TestChunkSize);
        var original = await EncryptAsync(beforeRotation, RandomNumberGenerator.GetBytes(120));

        var afterRotation = new EnvelopeEncryptor(new KeyRing([oldKey, newKey], "k2"), TestChunkSize);
        using var source = new MemoryStream(original);
        using var destination = new MemoryStream();
        await afterRotation.RewrapAsync(source, destination);

        await Assert.ThrowsAsync<UnknownKeyException>(() => DecryptAsync(beforeRotation, destination.ToArray()));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private const int FrameOverhead = 12 /* nonce */ + 1 /* final flag */ + 4 /* length */ + 16 /* tag */;

    private static async Task<int> HeaderLengthOf(byte[] ciphertext)
    {
        using var stream = new MemoryStream(ciphertext);
        var header = await EncryptedObjectHeader.ReadAsync(stream);
        return header.Length;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Stands in for the upload streams the API receives, whose length is not
    /// known before the body has been read.
    /// </summary>
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        // Hand out short reads on purpose, so the chunk filling loop is exercised.
        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(buffer, offset, Math.Min(count, 7));

        public override void Flush() => _inner.Flush();
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
