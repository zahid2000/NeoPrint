using NeoPrint.Crypto;

namespace NeoPrint.Kiosk.Core;

/// <summary>
/// The kiosk's own service PIN (T-334): the way into the hidden admin menu
/// that works before the kiosk is activated and while it cannot reach the
/// server - when the admin PIN from the backend is not there yet.
/// </summary>
/// <remarks>
/// <para>
/// Set once per machine while it is provisioned (<c>set-service-pin</c> on the
/// agent, run as root), and kept only as a PBKDF2 hash (<see cref="SecretHash"/>)
/// in a file the administrator owns and the kiosk account can only read. It is
/// never in the build, on the setup stick or on the server: one machine's PIN
/// opens that machine and no other. A golden image leaves it behind
/// (<c>prepare-image</c>); each clone is given its own.
/// </para>
/// <para>
/// The digits are never logged or written down anywhere but as the hash.
/// </para>
/// </remarks>
public static class ServicePin
{
    public const int MinLength = 6;

    public const int MaxLength = 12;

    /// <summary>
    /// <c>/etc/neoprint/service-pin</c> on Linux (root, readable by the kiosk
    /// group), <c>%ProgramData%\NeoPrint\service-pin</c> on Windows.
    /// </summary>
    public static string DefaultPath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NeoPrint", "service-pin")
        : "/etc/neoprint/service-pin";

    /// <summary>Six to twelve digits: what the kiosk's PIN keypad can type.</summary>
    public static bool IsValid(string? pin)
        => pin is { Length: >= MinLength and <= MaxLength } && pin.All(char.IsAsciiDigit);

    /// <summary>The stored hash, or null when there is none or it cannot be read.</summary>
    public static string? ReadHash(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var hash = File.ReadAllText(path).Trim();
            return hash.StartsWith("pbkdf2$", StringComparison.Ordinal) ? hash : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the hash of <paramref name="pin"/> to <paramref name="path"/>, whole
    /// or not at all; on Linux the file is made owner-writable and group-readable.
    /// </summary>
    public static void Write(string path, string pin)
    {
        if (!IsValid(pin))
        {
            throw new ArgumentException($"A service PIN is {MinLength} to {MaxLength} digits.", nameof(pin));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".partial";
        File.WriteAllText(partial, SecretHash.Hash(pin) + "\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(partial, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }

        File.Move(partial, path, overwrite: true);
    }
}
