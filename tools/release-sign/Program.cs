using System.Security.Cryptography;
using System.Text;

using NeoPrint.Crypto.Releases;
using NeoPrint.Kiosk.Core.Updates;

namespace NeoPrint.ReleaseSign;

/// <summary>
/// release-sign keygen   --name current --out DIR
/// release-sign manifest --package FILE --component App|Agent|Provisioning --platform win-x64|linux-x64
///                       --version X.Y.Z --min-agent-version X.Y.Z [--commit SHA] [--out manifest.json]
/// release-sign sign     --manifest manifest.json --package FILE --key current.private.pem
/// release-sign verify   --manifest manifest.json --package FILE [--signature manifest.json.sig]
///                       [--public-key a.pem]... [--installed X.Y.Z] [--agent-version X.Y.Z] [--withdrawn SHA256]...
/// release-sign check-key --key current.private.pem   (does a backup copy open, and which key is it)
///
/// The passphrase of a private key is asked for on the terminal (or read from
/// standard input when it is redirected) - never an argument or an environment
/// variable, which end up in shell history and process listings.
/// </summary>
public static class Program
{
    private const int MinPassphraseLength = 16;

    // OWASP's 2023 figure for PBKDF2-HMAC-SHA256.
    private static readonly PbeParameters KeyEncryption = new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args.Skip(1));
            return args.FirstOrDefault() switch
            {
                "keygen" => KeyGen(options),
                "manifest" => await ManifestAsync(options),
                "sign" => await SignAsync(options),
                "verify" => await VerifyAsync(options),
                "check-key" => await CheckKeyAsync(options),
                _ => Usage(),
            };
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: release-sign keygen|manifest|sign|verify|check-key ... (docs/RELEASE_SIGNING.md)");
        return 2;
    }

    private static int KeyGen(Options options)
    {
        var name = options.Required("name");
        if (name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            throw new UsageException("--name is letters, digits and '-' (e.g. current, next).");
        }

        var dir = options.Required("out");
        var privatePath = Path.Combine(dir, $"{name}.private.pem");
        var publicPath = Path.Combine(dir, $"{name}.public.pem");
        if (File.Exists(privatePath) || File.Exists(publicPath))
        {
            throw new UsageException($"{privatePath} or {publicPath} exists already; a key is never overwritten.");
        }

        WarnIfDomainJoined();
        var passphrase = AskPassphrase($"Passphrase for the '{name}' key (at least {MinPassphraseLength} characters): ", confirm: true);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(dir);
        File.WriteAllText(privatePath, key.ExportEncryptedPkcs8PrivateKeyPem(passphrase, KeyEncryption) + "\n");
        var publicKey = ReleasePublicKey.FromKey(key);
        File.WriteAllText(publicPath, publicKey.ToPem() + "\n");

        Console.WriteLine($"Key id:      {publicKey.KeyId}");
        Console.WriteLine($"Private key: {privatePath}  (encrypted; stays on this machine and its backups)");
        Console.WriteLine($"Public key:  {publicPath}  (this one is given out)");
        return 0;
    }

    private static async Task<int> ManifestAsync(Options options)
    {
        var package = options.Required("package");
        if (!Enum.GetNames<ReleaseComponent>().Contains(options.Required("component"), StringComparer.Ordinal))
        {
            throw new UsageException("--component is App, Agent or Provisioning.");
        }

        var info = new FileInfo(package);
        if (!info.Exists)
        {
            throw new UsageException($"{package} does not exist.");
        }

        await using var stream = info.OpenRead();
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        var manifest = new ReleaseManifest(
            Enum.Parse<ReleaseComponent>(options.Required("component")),
            options.Required("platform"),
            Version(options.Required("version"), "--version"),
            Version(options.Required("min-agent-version"), "--min-agent-version"),
            info.Name,
            info.Length,
            sha256,
            options.Optional("commit")?.ToLowerInvariant(),
            DateTimeOffset.UtcNow);

        var bytes = manifest.ToBytes();
        try
        {
            // What CI writes is what a kiosk will read.
            ReleaseManifest.Parse(bytes);
        }
        catch (FormatException e)
        {
            throw new UsageException(e.Message);
        }

        var output = options.Optional("out") ?? "manifest.json";
        await File.WriteAllBytesAsync(output, bytes);
        Console.WriteLine($"{output}: {manifest.Component} {manifest.Version} {manifest.Platform}, {manifest.File}, {manifest.Size} bytes, SHA-256 {sha256}");
        return 0;
    }

    private static async Task<int> SignAsync(Options options)
    {
        var manifestPath = options.Required("manifest");
        var signaturePath = manifestPath + ".sig";
        if (File.Exists(signaturePath))
        {
            throw new UsageException($"{signaturePath} exists already.");
        }

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        ReleaseManifest manifest;
        try
        {
            manifest = ReleaseManifest.Parse(manifestBytes);
        }
        catch (FormatException e)
        {
            throw new UsageException($"{manifestPath}: {e.Message}");
        }

        // Signing says "this exact file is ours": the package in hand has to be it.
        var package = new FileInfo(options.Required("package"));
        await using (var stream = package.OpenRead())
        {
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            if (package.Length != manifest.Size || sha256 != manifest.Sha256)
            {
                Console.Error.WriteLine($"{package.Name} is not the file in the manifest: SHA-256 {sha256}, the manifest says {manifest.Sha256}. Nothing was signed.");
                return 1;
            }
        }

        Console.WriteLine($"Signing {manifest.Component} {manifest.Version} for {manifest.Platform}");
        Console.WriteLine($"  file     {manifest.File} ({manifest.Size} bytes)");
        Console.WriteLine($"  SHA-256  {manifest.Sha256}");
        Console.WriteLine($"  commit   {manifest.Commit ?? "-"}, built {manifest.CreatedAt:yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine("Compare the SHA-256 with the one the CI run printed before going on.");

        WarnIfDomainJoined();
        var keyPem = await File.ReadAllTextAsync(options.Required("key"));
        var passphrase = AskPassphrase("Passphrase of the key: ", confirm: false);
        using var key = ECDsa.Create();
        try
        {
            key.ImportFromEncryptedPem(keyPem, passphrase);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            Console.Error.WriteLine("The key could not be opened: wrong passphrase, or not an encrypted private key. Nothing was signed.");
            return 1;
        }

        var signature = ReleaseSignature.Sign(key, manifestBytes);
        var publicKey = ReleasePublicKey.FromKey(key);
        if (!signature.IsValidFor(manifestBytes, publicKey))
        {
            Console.Error.WriteLine("The signature did not verify. Nothing was written.");
            return 1;
        }

        await File.WriteAllBytesAsync(signaturePath, signature.ToBytes());
        Console.WriteLine($"{signaturePath}: signed with key {publicKey.KeyId}" +
                          (ReleaseKeys.Trusted.Any(k => k.KeyId == publicKey.KeyId) ? "" : " - NOT one of the keys the kiosks carry"));
        return 0;
    }

    private static async Task<int> VerifyAsync(Options options)
    {
        var manifestPath = options.Required("manifest");
        var keys = options.All("public-key").Select(path => ReleasePublicKey.FromPem(File.ReadAllText(path))).ToList();
        if (keys.Count == 0)
        {
            keys = ReleaseKeys.Trusted.ToList();
            Console.WriteLine($"Keys: the kiosk's own ({(keys.Count == 0 ? "none set yet" : string.Join(", ", keys.Select(k => k.KeyId)))})");
        }
        else
        {
            Console.WriteLine($"Keys: {string.Join(", ", keys.Select(k => k.KeyId))}");
        }

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var signatureBytes = await File.ReadAllBytesAsync(options.Optional("signature") ?? manifestPath + ".sig");

        // The target is whatever the manifest claims - the signature is checked before any of it is believed.
        ReleaseExpectation expected;
        try
        {
            var claimed = ReleaseManifest.Parse(manifestBytes);
            expected = new ReleaseExpectation(
                claimed.Component,
                claimed.Platform,
                options.Optional("installed") is { } installed ? Version(installed, "--installed") : null,
                options.Optional("agent-version") is { } agent ? Version(agent, "--agent-version") : claimed.MinAgentVersion,
                options.All("withdrawn"));
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine($"REFUSED (MalformedManifest): {e.Message}");
            return 1;
        }

        await using var package = File.OpenRead(options.Required("package"));
        var check = await new ReleaseVerifier(keys).CheckAsync(manifestBytes, signatureBytes, package, expected);
        if (check.Accepted)
        {
            Console.WriteLine($"OK: {check.Detail}");
            return 0;
        }

        Console.Error.WriteLine($"REFUSED ({check.Rejection}): {check.Detail}");
        return 1;
    }

    private static async Task<int> CheckKeyAsync(Options options)
    {
        var keyPem = await File.ReadAllTextAsync(options.Required("key"));
        var passphrase = AskPassphrase("Passphrase of the key: ", confirm: false);
        using var key = ECDsa.Create();
        try
        {
            key.ImportFromEncryptedPem(keyPem, passphrase);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            Console.Error.WriteLine("The key could not be opened: wrong passphrase, or not an encrypted private key.");
            return 1;
        }

        var publicKey = ReleasePublicKey.FromKey(key);
        var role = ReleaseKeys.Current.Length > 0 && ReleasePublicKey.FromPem(ReleaseKeys.Current).KeyId == publicKey.KeyId ? "the kiosks' current key"
            : ReleaseKeys.Next.Length > 0 && ReleasePublicKey.FromPem(ReleaseKeys.Next).KeyId == publicKey.KeyId ? "the kiosks' next key"
            : "not a key the kiosks carry";
        Console.WriteLine($"OK: key {publicKey.KeyId} opens ({role}).");
        return 0;
    }

    private static ReleaseVersion Version(string text, string option) =>
        ReleaseVersion.TryParse(text, out var version) ? version : throw new UsageException($"{option} '{text}' is not a release version.");

    private static string AskPassphrase(string prompt, bool confirm)
    {
        var passphrase = ReadSecret(prompt);
        if (confirm)
        {
            if (passphrase.Length < MinPassphraseLength)
            {
                throw new UsageException($"The passphrase is shorter than {MinPassphraseLength} characters. No key was written.");
            }

            if (ReadSecret("Once more: ") != passphrase)
            {
                throw new UsageException("The two passphrases differ. No key was written.");
            }
        }

        return passphrase;
    }

    private static string ReadSecret(string prompt)
    {
        Console.Error.Write(prompt);
        if (Console.IsInputRedirected)
        {
            var line = Console.ReadLine() ?? "";
            Console.Error.WriteLine();
            return line;
        }

        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return text.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
    }

    // docs/RELEASE_SIGNING.md: the key does not live on a machine an organisation's administrators can reach.
    private static void WarnIfDomainJoined()
    {
        if (OperatingSystem.IsWindows()
            && !string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"WARNING: this account belongs to the domain '{Environment.UserDomainName}'. " +
                                    "The release key belongs on a machine outside any domain (docs/RELEASE_SIGNING.md).");
        }
    }

    private sealed class UsageException(string message) : Exception(message);

    private sealed class Options
    {
        private readonly List<(string Name, string Value)> _values = [];

        public static Options Parse(IEnumerable<string> args)
        {
            var options = new Options();
            using var e = args.GetEnumerator();
            while (e.MoveNext())
            {
                var name = e.Current;
                if (!name.StartsWith("--", StringComparison.Ordinal) || !e.MoveNext())
                {
                    throw new UsageException($"'{name}' is not an --option with a value.");
                }

                options._values.Add((name[2..], e.Current));
            }

            return options;
        }

        public string Required(string name) =>
            Optional(name) ?? throw new UsageException($"--{name} is required.");

        public string? Optional(string name) =>
            _values.LastOrDefault(v => v.Name == name).Value;

        public IReadOnlyCollection<string> All(string name) =>
            _values.Where(v => v.Name == name).Select(v => v.Value).ToList();
    }
}
