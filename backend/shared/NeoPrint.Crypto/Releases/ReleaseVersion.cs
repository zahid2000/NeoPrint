using System.Globalization;
using System.Text.RegularExpressions;

namespace NeoPrint.Crypto.Releases;

/// <summary>
/// A release version: SemVer 2.0 without build metadata (<c>1.4.0</c>,
/// <c>1.5.0-pilot.2</c>). Build metadata is refused because SemVer ignores it
/// when comparing, and two packages that compare equal must not both exist.
/// </summary>
public sealed partial record ReleaseVersion(int Major, int Minor, int Patch, string? PreRelease) : IComparable<ReleaseVersion>
{
    [GeneratedRegex(@"^(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = null!;
        if (text is null || text.Length > 64)
        {
            return false;
        }

        var match = Pattern().Match(text);
        if (!match.Success)
        {
            return false;
        }

        version = new ReleaseVersion(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
            match.Groups[4].Success ? match.Groups[4].Value : null);
        return true;
    }

    public static ReleaseVersion Parse(string text) =>
        TryParse(text, out var version)
            ? version
            : throw new FormatException($"'{text}' is not a release version (major.minor.patch, optionally -prerelease).");

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0)
        {
            return core;
        }

        // A pre-release comes before its release: 1.0.0-pilot.1 < 1.0.0.
        if (PreRelease is null || other.PreRelease is null)
        {
            return (PreRelease is null).CompareTo(other.PreRelease is null);
        }

        var mine = PreRelease.Split('.');
        var theirs = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            var order = CompareIdentifier(mine[i], theirs[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return mine.Length.CompareTo(theirs.Length);
    }

    private static int CompareIdentifier(string a, string b)
    {
        var aNumeric = a.All(char.IsAsciiDigit);
        var bNumeric = b.All(char.IsAsciiDigit);
        if (aNumeric && bNumeric)
        {
            // Compared as numbers without parsing: no leading zeros, so the longer is larger.
            return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
        }

        // Numeric identifiers come before alphanumeric ones.
        return aNumeric != bNumeric ? (aNumeric ? -1 : 1) : string.CompareOrdinal(a, b);
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        PreRelease is null
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{PreRelease}");
}
