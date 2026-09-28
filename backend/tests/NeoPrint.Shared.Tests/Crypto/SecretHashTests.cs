using NeoPrint.Crypto;

namespace NeoPrint.Shared.Tests.Crypto;

/// <summary>
/// The admin PIN's hash: made by the backend, checked by the kiosk (plan
/// section 7.11), so both read one format.
/// </summary>
public sealed class SecretHashTests
{
    [Fact]
    public void A_pin_verifies_against_its_own_hash_and_no_other()
    {
        var hash = SecretHash.Hash("135790");

        hash.Should().StartWith("pbkdf2$sha256$210000$");
        SecretHash.Verify("135790", hash).Should().BeTrue();
        SecretHash.Verify("135791", hash).Should().BeFalse();
        SecretHash.Verify(" 135790", hash).Should().BeFalse();
    }

    [Fact]
    public void The_same_pin_is_salted_differently_each_time()
        => SecretHash.Hash("135790").Should().NotBe(SecretHash.Hash("135790"));

    [Fact]
    public void A_hash_made_with_fewer_iterations_still_verifies()
    {
        // The count travels with the hash: raising it later keeps old PINs working.
        var salt = new byte[16];
        var key = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            "4321"u8.ToArray(), salt, 1000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        var stored = $"pbkdf2$sha256$1000${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";

        SecretHash.Verify("4321", stored).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1$whatever")]
    [InlineData("pbkdf2$sha256$abc$AAAA$AAAA")]
    [InlineData("pbkdf2$sha256$1000$not-base64$AAAA")]
    public void Anything_that_is_not_a_hash_verifies_nothing(string? stored)
        => SecretHash.Verify("135790", stored).Should().BeFalse();
}
