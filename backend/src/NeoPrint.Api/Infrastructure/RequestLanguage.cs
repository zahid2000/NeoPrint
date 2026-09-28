using Microsoft.Extensions.Primitives;

using NeoPrint.Application.Localization;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// Picks the language the response's text is written in (plan section 2.1).
/// </summary>
/// <remarks>
/// <para>
/// From <c>Accept-Language</c>: the mobile app, the admin panel and the kiosk
/// each send the language their interface is showing, so an error reads in
/// the same language as the screen it lands on. The best-weighted supported
/// language wins; anything else falls back to Azerbaijani.
/// </para>
/// <para>
/// Runs before the exception handler, because the language is an ambient
/// value that flows into what runs inside this middleware and not back out.
/// Mail is different: it goes out in the recipient's own
/// <c>users.language</c>, whatever the request was in.
/// </para>
/// </remarks>
public static class RequestLanguage
{
    public static IApplicationBuilder UseNeoPrintRequestLanguage(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            Messages.Current = Pick(context.Request.Headers.AcceptLanguage) ?? Messages.DefaultLanguage;
            context.Response.Headers.ContentLanguage = Messages.Current;
            await next(context).ConfigureAwait(false);
        });

    /// <summary>The supported language with the highest weight, or null.</summary>
    /// <example><c>en-US,en;q=0.9,az;q=0.8</c> → <c>en</c>; <c>de,ru;q=0.5</c> → <c>ru</c>.</example>
    public static string? Pick(StringValues header)
    {
        string? best = null;
        var bestWeight = 0.0;

        foreach (var value in header)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split(';', StringSplitOptions.TrimEntries);
                var language = Messages.Supported(parts[0]);

                if (language is null)
                {
                    continue;
                }

                var weight = 1.0;

                foreach (var parameter in parts.Skip(1))
                {
                    if (parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                        && double.TryParse(
                            parameter.AsSpan(2),
                            System.Globalization.NumberStyles.AllowDecimalPoint,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var parsed))
                    {
                        weight = parsed;
                    }
                }

                // Earlier entries win a tie, as the header lists them in order of preference.
                if (weight > bestWeight)
                {
                    best = language;
                    bestWeight = weight;
                }
            }
        }

        return best;
    }
}
