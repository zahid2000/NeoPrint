using FluentAssertions;

using NeoPrint.Crypto.Privacy;

namespace NeoPrint.Shared.Tests.Crypto;

/// <summary>
/// What a kiosk log may not carry (T-321, docs/KIOSK_UPDATES.md section 6):
/// each kind of value is taken out, and an ordinary line stays as it was.
/// </summary>
public sealed class LogRedactorTests
{
    [Theory]
    [InlineData("GET /api/kiosks/me/update Authorization: Bearer eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl", "eyJhbGci")]
    [InlineData("token eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl expired", "eyJhbGci")]
    [InlineData("{\"accessToken\":\"abc.def.ghi\",\"expiresAt\":\"2026-10-25\"}", "abc.def.ghi")]
    [InlineData("refresh_token=Zm9vYmFyYmF6&x=1", "Zm9vYmFyYmF6")]
    [InlineData("login failed password=hunter2!", "hunter2")]
    [InlineData("\"pin\": \"4711\"", "4711")]
    [InlineData("passphrase: correct horse", "correct")]
    [InlineData("scanned NPU:4f9a8b7c6d5e4f3a2b1c", "4f9a8b7c6d5e")]
    [InlineData("receipt sent to anar.mammadov@example.az", "anar.mammadov")]
    [InlineData("SMS to +994 50 123 45 67 queued", "123 45 67")]
    [InlineData("call 0501234567 back", "0501234567")]
    [InlineData("phone (055) 123-45-67", "123-45-67")]
    [InlineData("intl +4915112345678", "4915112345678")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nMIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg\n-----END PRIVATE KEY-----", "MIGHAgEA")]
    public void A_sensitive_value_is_taken_out(string line, string secret)
    {
        var redacted = LogRedactor.Redact(line, out var replaced);

        redacted.Should().NotContain(secret);
        redacted.Should().Contain(LogRedactor.Mark);
        replaced.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_long_encoded_block_that_could_be_file_content_is_taken_out()
    {
        var content = Convert.ToBase64String(Enumerable.Range(0, 200).Select(i => (byte)i).ToArray());

        LogRedactor.Redact($"upload body {content}").Should().NotContain(content[..40]);
    }

    [Theory]
    [InlineData("login password=Sup3rS3cret ok")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJwcm9iZSJ9.c2lnbmF0dXJl phone +994501234567")]
    [InlineData("{\"token\":\"abc\",\"email\":\"probe.user@example.com\"}")]
    public void A_line_already_redacted_comes_out_the_same_and_counts_nothing(string line)
    {
        var once = LogRedactor.Redact(line);

        LogRedactor.Redact(once, out var replaced).Should().Be(once);
        replaced.Should().Be(0, "the kiosk and the backend both redact; the second pass finds nothing new");
        once.Should().NotContain("]]");
    }

    [Theory]
    [InlineData("Heartbeat answered in 42 ms; next in 15 s.")]
    [InlineData("2026-10-25T02:14:07.123Z Applying App 0.8.0-pilot.2 (from 0.8.0-pilot.1)")]
    [InlineData("Package sha256 9fa8f1290eaced3aba98bbf842d7da2355f0fadbfa55f88c680c6ef6ad3f8996 verified")]
    [InlineData("Job 6c0d5b4f-2e3a-4f9b-8d8c-3b2f4a5e6d71 printed 12 pages on HP_LaserJet_M404")]
    [InlineData("Free disk 12884901888 bytes, need 157286400")]
    [InlineData("tokens left: 3, spinner=on")]
    public void An_ordinary_line_stays_as_it_was(string line)
    {
        LogRedactor.Redact(line, out var replaced).Should().Be(line);
        replaced.Should().Be(0);
    }
}
