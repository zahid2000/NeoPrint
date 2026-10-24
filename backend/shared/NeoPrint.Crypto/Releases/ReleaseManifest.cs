using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NeoPrint.Crypto.Releases;

/// <summary>What a release package carries (plan section 7.12, <c>releases.Component</c>).</summary>
public enum ReleaseComponent
{
    App,
    Agent,
    Provisioning,
}

/// <summary>
/// The manifest of one release package: what it is, for which platform, and
/// the SHA-256 of the package file (docs/RELEASE_SIGNING.md). CI writes it,
/// the release key signs its exact bytes, and the kiosk reads it only after the
/// signature has been checked. <see cref="KeySet"/> says which kind of build
/// the package is (T-316): a staging package carries the staging keys and is
/// signed with a staging key; a production one, the production keys.
/// </summary>
public sealed record ReleaseManifest(
    ReleaseComponent Component,
    string Platform,
    string KeySet,
    ReleaseVersion Version,
    ReleaseVersion MinAgentVersion,
    string File,
    long Size,
    string Sha256,
    string? Commit,
    DateTimeOffset CreatedAt)
{
    /// <summary>The only manifest format written today.</summary>
    public const int CurrentSchema = 1;

    /// <summary>The platforms a kiosk package is built for (plan section 7.6).</summary>
    public static readonly IReadOnlyList<string> Platforms = ["win-x64", "linux-x64"];

    private static readonly string[] Fields =
        ["schema", "component", "platform", "keySet", "version", "minAgentVersion", "file", "size", "sha256", "commit", "createdAt"];

    /// <summary>The manifest as CI writes it: indented UTF-8 JSON with LF line ends.</summary>
    public byte[] ToBytes()
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("schema", CurrentSchema);
            json.WriteString("component", Component.ToString());
            json.WriteString("platform", Platform);
            json.WriteString("keySet", KeySet);
            json.WriteString("version", Version.ToString());
            json.WriteString("minAgentVersion", MinAgentVersion.ToString());
            json.WriteString("file", File);
            json.WriteNumber("size", Size);
            json.WriteString("sha256", Sha256);
            if (Commit is null)
            {
                json.WriteNull("commit");
            }
            else
            {
                json.WriteString("commit", Commit);
            }

            json.WriteString("createdAt", CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            json.WriteEndObject();
        }

        // Utf8JsonWriter follows the platform's line end; the signed bytes must not.
        var text = Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// Reads a manifest strictly: every field present, nothing unknown, every
    /// value in range. Anything else is a <see cref="FormatException"/>.
    /// </summary>
    public static ReleaseManifest Parse(ReadOnlySpan<byte> bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
        }
        catch (JsonException e)
        {
            throw new FormatException("The manifest is not JSON.", e);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("The manifest is not a JSON object.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!Fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                {
                    throw new FormatException($"The manifest has an unknown or repeated field '{property.Name}'.");
                }
            }

            if (seen.Count != Fields.Length)
            {
                throw new FormatException($"The manifest lacks {string.Join(", ", Fields.Except(seen))}.");
            }

            if (Number(root, "schema") != CurrentSchema)
            {
                throw new FormatException($"The manifest is schema {Number(root, "schema")}; this build reads schema {CurrentSchema}.");
            }

            // By name only: Enum.TryParse would also take "1" and "App,Agent".
            var componentName = Text(root, "component");
            if (!Enum.GetNames<ReleaseComponent>().Contains(componentName, StringComparer.Ordinal))
            {
                throw new FormatException("The manifest's component is not App, Agent or Provisioning.");
            }

            var platform = Text(root, "platform");
            if (!Platforms.Contains(platform, StringComparer.Ordinal))
            {
                throw new FormatException($"The manifest's platform '{platform}' is not one of {string.Join(", ", Platforms)}.");
            }

            var keySet = Text(root, "keySet");
            if (!ReleaseKeySet.IsName(keySet))
            {
                throw new FormatException("The manifest's keySet is not a key set name.");
            }

            var file = Text(root, "file");
            if (file.Length is 0 or > 200 || file != Path.GetFileName(file) || file.StartsWith('.')
                || file.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            {
                throw new FormatException("The manifest's file name is not a plain file name.");
            }

            var size = Number(root, "size");
            if (size <= 0)
            {
                throw new FormatException("The manifest's size is not positive.");
            }

            var sha256 = Text(root, "sha256");
            if (sha256.Length != 64 || sha256.Any(c => !char.IsAsciiHexDigitLower(c)))
            {
                throw new FormatException("The manifest's sha256 is not 64 lower-case hex digits.");
            }

            string? commit = null;
            if (root.GetProperty("commit").ValueKind != JsonValueKind.Null)
            {
                commit = Text(root, "commit");
                if (commit.Length is < 7 or > 64 || commit.Any(c => !char.IsAsciiHexDigitLower(c)))
                {
                    throw new FormatException("The manifest's commit is not a git commit id.");
                }
            }

            if (!DateTimeOffset.TryParseExact(Text(root, "createdAt"), "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var createdAt))
            {
                throw new FormatException("The manifest's createdAt is not a UTC time (yyyy-MM-ddTHH:mm:ssZ).");
            }

            return new ReleaseManifest(Enum.Parse<ReleaseComponent>(componentName), platform, keySet, VersionOf(root, "version"), VersionOf(root, "minAgentVersion"),
                file, size, sha256, commit, createdAt);
        }
    }

    private static string Text(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new FormatException($"The manifest's {name} is not a non-empty string.");
    }

    private static long Number(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : throw new FormatException($"The manifest's {name} is not a whole number.");
    }

    private static ReleaseVersion VersionOf(JsonElement root, string name) =>
        ReleaseVersion.TryParse(Text(root, name), out var version)
            ? version
            : throw new FormatException($"The manifest's {name} is not a release version.");
}
