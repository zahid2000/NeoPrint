using System.Text.RegularExpressions;

namespace NeoPrint.Crypto.Privacy;

/// <summary>
/// Takes out of a log line what must never leave a kiosk in its logs
/// (CLAUDE.md "Log-a yazılmır"; docs/KIOSK_UPDATES.md section 6): tokens,
/// passwords, keys, personal QR codes, phone numbers, e-mail addresses and
/// long encoded blocks that could be file content.
/// </summary>
/// <remarks>
/// The kiosk applies it when it writes a line and again before an upload; the
/// backend applies it once more to what it receives, so an older kiosk build
/// cannot put anything past it. It is deliberately greedy: a log line with a
/// value taken out is still a log line, a leaked token is not taken back.
/// </remarks>
public static partial class LogRedactor
{
    /// <summary>What a value is replaced with.</summary>
    public const string Mark = "[redacted]";

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]+-----[\s\S]*?(?:-----END [A-Z0-9 ]+-----|$)", RegexOptions.CultureInvariant)]
    private static partial Regex PemBlock();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.CultureInvariant)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    // A value already replaced is left alone: a line passes here up to three
    // times (kiosk write, kiosk upload, backend) and must come out the same.
    [GeneratedRegex(
        @"(?i)(""?\b(?:access_?token|refresh_?token|id_?token|token|password|passwd|pwd|secret|client_?secret|api_?key|authorization|passphrase|private_?key|pin|pin_?hash|otp|code_?verifier)""?\s*[:=]\s*)(?!\[redacted\])(""(?:[^""\\]|\\.)*""|[^\s,;&}\]]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretValue();

    [GeneratedRegex(@"\bNP[A-Z]?:[A-Za-z0-9_\-.~+/=]{6,}", RegexOptions.CultureInvariant)]
    private static partial Regex PersonalQr();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    // Azerbaijani numbers in the ways people write them (+994 50 123 45 67,
    // 0501234567, (050) 123-45-67), and any number with an international "+".
    [GeneratedRegex(
        @"(?<![\d.])(?:(?:\+|00)?994|0)[\s\-]?\(?(?:10|12|50|51|55|60|70|77|99)\)?[\s\-]?\d{3}[\s\-]?\d{2}[\s\-]?\d{2}(?![\d.])|\+\d[\d\s\-]{8,16}\d",
        RegexOptions.CultureInvariant)]
    private static partial Regex Phone();

    // Long runs of base64 / hex: file content, keys, signatures. A SHA-256 in
    // hex (64 characters) stays - it is a checksum, not a secret.
    [GeneratedRegex(@"[A-Za-z0-9+/_\-]{100,}={0,2}", RegexOptions.CultureInvariant)]
    private static partial Regex EncodedBlock();

    /// <summary>The line with every sensitive value replaced by <see cref="Mark"/>.</summary>
    public static string Redact(string line) => Redact(line, out _);

    /// <summary>The line with every sensitive value replaced; <paramref name="replaced"/> says how many were.</summary>
    public static string Redact(string line, out int replaced)
    {
        ArgumentNullException.ThrowIfNull(line);

        var count = 0;
        string Count(string value)
        {
            count++;
            return value;
        }

        var result = PemBlock().Replace(line, _ => Count(Mark));
        result = Bearer().Replace(result, _ => Count("Bearer " + Mark));
        result = Jwt().Replace(result, _ => Count(Mark));
        result = SecretValue().Replace(result, match => Count(match.Groups[1].Value + Mark));
        result = PersonalQr().Replace(result, _ => Count(Mark));
        result = Email().Replace(result, _ => Count(Mark));
        result = Phone().Replace(result, _ => Count(Mark));
        result = EncodedBlock().Replace(result, _ => Count(Mark));

        replaced = count;
        return result;
    }
}
