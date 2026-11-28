using System.Text.Json;

using NeoPrint.Crypto.Releases;

namespace NeoPrint.Application.Clients;

/// <summary>
/// The oldest kiosk and mobile app the backend still serves (ADR-0009,
/// T-492): <c>minSupported</c> of <c>contracts/client-api.json</c>, built in.
/// </summary>
/// <remarks>
/// The same file the contract tests read, so the version the backend says it
/// supports and the API it keeps for it cannot drift apart. Raising it is a
/// change to that file - reviewed, and deployed with the backend that may then
/// drop what older clients called.
/// </remarks>
public static class SupportedClients
{
    /// <summary>The header the mobile app sends its version in.</summary>
    public const string AppVersionHeader = "X-App-Version";

    private const string Resource = "NeoPrint.Application.client-api.json";

    private static readonly (ReleaseVersion? Kiosk, ReleaseVersion? Mobile) Minimums = Read();

    /// <summary>The oldest kiosk application supported; null when the file says none.</summary>
    public static ReleaseVersion? Kiosk => Minimums.Kiosk;

    /// <summary>The oldest mobile app supported by the API itself (the panel may ask for a newer one).</summary>
    public static ReleaseVersion? Mobile => Minimums.Mobile;

    /// <summary>Whether a client of that version is supported; null when either is not a release version.</summary>
    public static bool? IsSupported(string? version, ReleaseVersion? minimum)
        => minimum is null || !ReleaseVersion.TryParse(version, out var parsed) ? null : parsed.CompareTo(minimum) >= 0;

    /// <summary>The newer of two minimums; either may be missing.</summary>
    public static ReleaseVersion? Newest(ReleaseVersion? first, ReleaseVersion? second)
        => first is null ? second : second is null || first.CompareTo(second) >= 0 ? first : second;

    private static (ReleaseVersion?, ReleaseVersion?) Read()
    {
        using var stream = typeof(SupportedClients).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"The client contract ({Resource}) is not built into the application.");
        using var document = JsonDocument.Parse(stream);

        ReleaseVersion? Of(string client)
            => document.RootElement.TryGetProperty("minSupported", out var minimums)
               && minimums.TryGetProperty(client, out var value)
               && value.ValueKind == JsonValueKind.String
                ? ReleaseVersion.Parse(value.GetString()!)
                : null;

        return (Of("kiosk"), Of("mobile"));
    }
}
