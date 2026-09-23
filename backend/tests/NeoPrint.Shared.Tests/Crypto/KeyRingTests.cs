using System.Security.Cryptography;

using NeoPrint.Crypto;

namespace NeoPrint.Shared.Tests.Crypto;

public sealed class KeyEncryptionKeyTests
{
    [Fact]
    public void Accepts_a_256_bit_key()
    {
        var key = new KeyEncryptionKey("k1", RandomNumberGenerator.GetBytes(32));

        key.Id.Should().Be("k1");
        key.Key.Length.Should().Be(32);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(31)]
    [InlineData(33)]
    public void Rejects_a_key_that_is_not_256_bit(int length)
    {
        var act = () => new KeyEncryptionKey("k1", RandomNumberGenerator.GetBytes(length));

        act.Should().Throw<ArgumentException>().WithMessage("*256 bit*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_a_blank_id(string id)
    {
        var act = () => new KeyEncryptionKey(id, RandomNumberGenerator.GetBytes(32));

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("k 1")]
    [InlineData("k/1")]
    [InlineData("k.1")]
    [InlineData("kə1")]
    public void Rejects_an_id_that_would_not_survive_the_object_header(string id)
    {
        // The id is written into every object header as ASCII, so anything
        // outside a conservative character set has to be refused at the source.
        var act = () => new KeyEncryptionKey(id, RandomNumberGenerator.GetBytes(32));

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("k1")]
    [InlineData("2026-09")]
    [InlineData("kek_2")]
    public void Accepts_the_id_shapes_we_actually_use(string id)
    {
        var act = () => new KeyEncryptionKey(id, RandomNumberGenerator.GetBytes(32));

        act.Should().NotThrow();
    }

    [Fact]
    public void Reads_the_base64_form_the_secret_store_hands_out()
    {
        var material = RandomNumberGenerator.GetBytes(32);

        var key = KeyEncryptionKey.FromBase64("k1", Convert.ToBase64String(material));

        key.Key.ToArray().Should().Equal(material);
    }

    [Fact]
    public void Reports_a_malformed_base64_key_clearly()
    {
        var act = () => KeyEncryptionKey.FromBase64("k1", "not base64 at all!!");

        act.Should().Throw<CryptoFormatException>().WithMessage("*not valid base64*");
    }

    [Fact]
    public void Reports_a_base64_key_of_the_wrong_length_clearly()
    {
        var act = () => KeyEncryptionKey.FromBase64("k1", Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));

        act.Should().Throw<CryptoFormatException>().WithMessage("*16 bytes*32*");
    }

    [Fact]
    public void Copies_the_key_material_so_the_caller_cannot_mutate_it_afterwards()
    {
        var material = RandomNumberGenerator.GetBytes(32);
        var key = new KeyEncryptionKey("k1", material);

        Array.Clear(material);

        key.Key.ToArray().Should().NotEqual(material);
    }
}

public sealed class KeyRingTests
{
    private static KeyEncryptionKey Key(string id) => new(id, RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Exposes_the_key_named_by_the_current_id()
    {
        var k1 = Key("k1");
        var k2 = Key("k2");

        var ring = new KeyRing([k1, k2], "k2");

        ring.Current.Should().BeSameAs(k2);
    }

    [Fact]
    public void Resolves_retired_keys_so_older_objects_stay_readable()
    {
        var ring = new KeyRing([Key("k1"), Key("k2")], "k2");

        ring.TryGet("k1", out var retired).Should().BeTrue();
        retired!.Id.Should().Be("k1");
    }

    [Fact]
    public void Reports_a_key_it_does_not_hold()
    {
        var ring = new KeyRing([Key("k1")], "k1");

        ring.TryGet("k9", out var missing).Should().BeFalse();
        missing.Should().BeNull();
    }

    [Fact]
    public void Refuses_a_current_id_that_names_no_key()
    {
        // Pointing NEOPRINT_KEK_CURRENT_ID at a key that was never added would
        // otherwise fail later, when the first object is written.
        var act = () => new KeyRing([Key("k1")], "k2");

        act.Should().Throw<ArgumentException>().WithMessage("*NEOPRINT_KEK__k2*");
    }

    [Fact]
    public void Refuses_an_empty_ring()
    {
        var act = () => new KeyRing([], "k1");

        act.Should().Throw<ArgumentException>();
    }
}
