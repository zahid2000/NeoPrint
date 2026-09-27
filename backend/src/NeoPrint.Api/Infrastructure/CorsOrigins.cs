using Microsoft.Extensions.Configuration;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// Which browser origins may call the API from another origin
/// (<c>Cors:AllowedOrigins</c>, docs/DECISIONS.md).
/// </summary>
/// <remarks>
/// <para>
/// The admin panel needs none of this: nginx serves it and the API from one
/// origin (plan section 8A.1). The consumer is the Flutter app built for the
/// web - in development from a localhost port the tooling picks, in a
/// deployment from an address someone configured on purpose.
/// </para>
/// <para>
/// So: in Development, loopback on any port (see
/// <see cref="DevelopmentCors.IsLoopbackOrigin"/>) plus whatever is
/// configured; everywhere else the configured origins and nothing else. A
/// configured origin is an exact scheme-host-port, <c>https</c> outside
/// Development, with no wildcard and no path - anything looser is refused at
/// start-up rather than quietly widening who can read the API's answers.
/// </para>
/// </remarks>
public sealed class CorsOrigins
{
    public const string PolicyName = "neoprint-cors";

    public const string SectionName = "Cors:AllowedOrigins";

    private readonly HashSet<string> _configured;
    private readonly bool _isDevelopment;

    private CorsOrigins(HashSet<string> configured, bool isDevelopment)
    {
        _configured = configured;
        _isDevelopment = isDevelopment;
    }

    /// <summary>The configured origins, normalised.</summary>
    public IReadOnlyCollection<string> Configured => _configured;

    /// <exception cref="InvalidOperationException">An origin in the configuration is not acceptable.</exception>
    public static CorsOrigins FromConfiguration(IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in configuration.GetSection(SectionName).Get<string[]>() ?? [])
        {
            origins.Add(Normalise(value, isDevelopment));
        }

        return new CorsOrigins(origins, isDevelopment);
    }

    public bool IsAllowed(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        if (_isDevelopment && DevelopmentCors.IsLoopbackOrigin(origin))
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
               && _configured.Contains(uri.GetLeftPart(UriPartial.Authority));
    }

    private static string Normalise(string value, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('*', StringComparison.Ordinal))
        {
            throw Invalid(value, "an exact origin is required, without a wildcard");
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            throw Invalid(value, "it must be an absolute http(s) origin");
        }

        if (uri.Scheme == "http" && !isDevelopment)
        {
            throw Invalid(value, "outside Development only https is accepted");
        }

        if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query))
        {
            throw Invalid(value, "an origin has no path or query");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static InvalidOperationException Invalid(string value, string reason)
        => new($"{SectionName} contains '{value}': {reason}.");
}
