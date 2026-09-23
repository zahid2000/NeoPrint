namespace NeoPrint.Application.Common;

/// <summary>
/// Masks personal data on its way into a log.
/// </summary>
/// <remarks>
/// <c>docs/OBSERVABILITY.md</c> keeps unnecessary personal information out of
/// the logs and <c>docs/SECURITY_THREAT_MODEL.md</c> asks for what remains to
/// be masked. An address still has to be recognisable to whoever is reading the
/// log - enough to tell one recipient from another and to match it against a
/// support request - without the log itself becoming a list of addresses.
/// </remarks>
public static class LogMasking
{
    /// <summary>
    /// Masks an email address: <c>zahid@example.com</c> becomes
    /// <c>z***d@example.com</c>.
    /// </summary>
    /// <remarks>
    /// The domain is kept whole: it is not personal data on its own, and it is
    /// what matters when a provider starts rejecting mail.
    /// </remarks>
    public static string Email(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return "(none)";
        }

        var at = address.LastIndexOf('@');

        if (at <= 0)
        {
            // Not an address at all. Something is wrong upstream, and whatever
            // it is should not be copied into the log.
            return "(invalid)";
        }

        var local = address[..at];
        var domain = address[at..];

        return local.Length switch
        {
            1 => $"*{domain}",
            2 => $"{local[0]}*{domain}",
            _ => $"{local[0]}***{local[^1]}{domain}",
        };
    }
}
