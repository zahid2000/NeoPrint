using NeoPrint.Application.Common;

namespace NeoPrint.Application.Tests.Common;

/// <summary>
/// Masking personal data before it reaches a log
/// (<c>docs/OBSERVABILITY.md</c>, <c>docs/SECURITY_THREAT_MODEL.md</c>).
/// </summary>
public sealed class LogMaskingTests
{
    [Theory]
    [InlineData("zahid@example.com", "z***d@example.com")]
    [InlineData("admin@neoprint.test", "a***n@neoprint.test")]
    [InlineData("ab@example.com", "a*@example.com")]
    [InlineData("a@example.com", "*@example.com")]
    public void An_address_keeps_its_domain_and_loses_its_name(string address, string expected)
        => LogMasking.Email(address).Should().Be(expected);

    [Fact]
    public void The_middle_of_a_long_name_is_not_revealed_by_its_length()
    {
        // Fixed-width mask: a three-star mask for every name of three
        // characters or more, so the log does not leak how long it was.
        LogMasking.Email("aleksandr@example.com").Should().Be("a***r@example.com");
        LogMasking.Email("abc@example.com").Should().Be("a***c@example.com");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_to_mask_says_so(string? address)
        => LogMasking.Email(address).Should().Be("(none)");

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("@example.com")]
    public void Something_that_is_not_an_address_is_not_copied_into_the_log(string address)
    {
        // Whatever it is, it is not what the caller thought it was, so none of
        // it should end up in the log.
        var masked = LogMasking.Email(address);

        masked.Should().Be("(invalid)");
        masked.Should().NotContain(address);
    }
}
