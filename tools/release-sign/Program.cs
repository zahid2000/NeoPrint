using System.Security.Cryptography;
using System.Text;

using NeoPrint.Crypto.Releases;
using NeoPrint.Kiosk.Core.Updates;

namespace NeoPrint.ReleaseSign;

/// <summary>
/// release-sign keygen       --name current --out DIR
/// release-sign check-key    --key current.private.pem   (does a backup copy open, and which key is it)
/// release-sign manifest     --package FILE --component App|Agent|Provisioning --platform win-x64|linux-x64
///                           --key-set staging|production --version X.Y.Z --min-agent-version X.Y.Z
///                           [--commit SHA] [--out manifest.json]
/// release-sign sign         --manifest manifest.json --package FILE --key current.private.pem   (production, offline)
/// release-sign sign-staging --dir ARTIFACT-DIR | --manifest manifest.json --package FILE [--key-dir DIR]
/// release-sign verify       --manifest manifest.json --package FILE --key-set staging|production | --public-key a.pem...
///                           [--signature manifest.json.sig] [--installed X.Y.Z] [--agent-version X.Y.Z] [--withdrawn SHA256]...
/// release-sign check-build  --dir PUBLISH-DIR --key-set staging|production
///
/// The passphrase of a production key is asked for on the terminal (or read
/// from standard input when it is redirected) - never an argument or an
/// environment variable, which end up in shell history and process listings.
/// Only sign-staging also takes it from NEOPRINT_STAGING_SIGNING_PASSPHRASE or
/// the key folder's passphrase.txt: a staging key signs only what test and
/// development kiosks install (docs/RELEASE_SIGNING.md, T-316).
/// </summary>
public static class Program
{
    private const int MinPassphraseLength = 16;

    private const string StagingPassphraseVariable = "NEOPRINT_STAGING_SIGNING_PASSPHRASE";

    // OWASP's 2023 figure for PBKDF2-HMAC-SHA256.
    private static readonly PbeParameters KeyEncryption = new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);

    private static readonly ReleaseKeySet Production =
        ReleaseKeySet.FromPems(ProductionReleaseKeys.Name, ProductionReleaseKeys.Current, ProductionReleaseKeys.Next);

    private static readonly ReleaseKeySet Staging =
        ReleaseKeySet.FromPems(StagingReleaseKeys.Name, StagingReleaseKeys.Current, StagingReleaseKeys.Next);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args.Skip(1));
            return args.FirstOrDefault() switch
            {
                "keygen" => KeyGen(options),
                "check-key" => await CheckKeyAsync(options),
                "manifest" => await ManifestAsync(options),
                "sign" => await SignAsync(options),
                "sign-staging" => await SignStagingAsync(options),
                "verify" => await VerifyAsync(options),
                "check-build" => CheckBuild(options),
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
        Console.Error.WriteLine("usage: release-sign keygen|check-key|manifest|sign|sign-staging|verify|check-build ... (docs/RELEASE_SIGNING.md)");
        return 2;
    }

    private static ReleaseKeySet Set(string name) => name switch
    {
        ProductionReleaseKeys.Name => Production,
        StagingReleaseKeys.Name => Staging,
        _ => throw new UsageException($"--key-set is {ProductionReleaseKeys.Name} or {StagingReleaseKeys.Name}."),
    };

    private static string Role(string keyId) =>
        Production.Holds(keyId) ? "a production key"
        : Staging.Holds(keyId) ? "a staging key"
        : "not a key any kiosk build carries";

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

    private static async Task<int> CheckKeyAsync(Options options)
    {
        var keyPem = await File.ReadAllTextAsync(options.Required("key"));
        using var key = OpenKey(keyPem, AskPassphrase("Passphrase of the key: ", confirm: false));
        if (key is null)
        {
            return 1;
        }

        var publicKey = ReleasePublicKey.FromKey(key);
        Console.WriteLine($"OK: key {publicKey.KeyId} opens ({Role(publicKey.KeyId)}).");
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
            Set(options.Required("key-set")).Name,
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
        Console.WriteLine($"{output}: {manifest.Component} {manifest.Version} {manifest.Platform} ({manifest.KeySet}), {manifest.File}, {manifest.Size} bytes, SHA-256 {sha256}");
        return 0;
    }

    /// <summary>The production ceremony: offline, a production key, the passphrase typed in.</summary>
    private static async Task<int> SignAsync(Options options)
    {
        var manifestPath = options.Required("manifest");
        var package = options.Required("package");
        var manifest = await ReadForSigningAsync(manifestPath, package);
        if (manifest is null)
        {
            return 1;
        }

        if (manifest.KeySet != Production.Name)
        {
            Console.Error.WriteLine($"{manifestPath} is a {manifest.KeySet} package; those are signed with sign-staging. Nothing was signed.");
            return 1;
        }

        Console.WriteLine("Compare the SHA-256 with the one the CI run printed before going on.");
        WarnIfDomainJoined();
        var keyPem = await File.ReadAllTextAsync(options.Required("key"));
        using var key = OpenKey(keyPem, AskPassphrase("Passphrase of the key: ", confirm: false));
        return key is null ? 1 : await WriteSignatureAsync(manifestPath, key, Production);
    }

    /// <summary>Staging packages for the test server and the development stack (T-316).</summary>
    private static async Task<int> SignStagingAsync(Options options)
    {
        var keyDir = options.Optional("key-dir")
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".neoprint", "release-keys", "staging");
        var keyPath = Path.Combine(keyDir, "current.private.pem");
        if (!File.Exists(keyPath))
        {
            throw new UsageException($"{keyPath} does not exist (docs/RELEASE_SIGNING.md, staging keys).");
        }

        var passphraseFile = Path.Combine(keyDir, "passphrase.txt");
        var passphrase = Environment.GetEnvironmentVariable(StagingPassphraseVariable) is { Length: > 0 } fromEnvironment ? fromEnvironment
            : File.Exists(passphraseFile) ? (await File.ReadAllTextAsync(passphraseFile)).Trim()
            : AskPassphrase("Passphrase of the staging key: ", confirm: false);
        using var key = OpenKey(await File.ReadAllTextAsync(keyPath), passphrase);
        if (key is null)
        {
            return 1;
        }

        if (!Staging.Holds(ReleasePublicKey.FromKey(key).KeyId))
        {
            Console.Error.WriteLine($"{keyPath} is {Role(ReleasePublicKey.FromKey(key).KeyId)}, not a staging key. Nothing was signed.");
            return 1;
        }

        var pairs = options.Optional("dir") is { } dir
            ? Directory.GetFiles(dir, "*.manifest.json").Order(StringComparer.Ordinal).Select(m => (Manifest: m, Package: m[..^".manifest.json".Length])).ToList()
            : [(options.Required("manifest"), options.Required("package"))];

        var failed = 0;
        var signed = 0;
        foreach (var (manifestPath, package) in pairs)
        {
            if (File.Exists(manifestPath + ".sig"))
            {
                Console.WriteLine($"{manifestPath}.sig exists already - skipped.");
                continue;
            }

            var manifest = await ReadForSigningAsync(manifestPath, package);
            if (manifest is null || manifest.KeySet != Staging.Name)
            {
                if (manifest is not null)
                {
                    Console.Error.WriteLine($"{manifestPath} is a {manifest.KeySet} package; a staging key never signs it.");
                }

                failed++;
                continue;
            }

            if (await WriteSignatureAsync(manifestPath, key, Staging) == 0)
            {
                signed++;
            }
            else
            {
                failed++;
            }
        }

        Console.WriteLine($"Signed {signed}, refused {failed}.");
        return failed == 0 && pairs.Count > 0 ? 0 : 1;
    }

    private static async Task<ReleaseManifest?> ReadForSigningAsync(string manifestPath, string packagePath)
    {
        ReleaseManifest manifest;
        try
        {
            manifest = ReleaseManifest.Parse(await File.ReadAllBytesAsync(manifestPath));
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine($"{manifestPath}: {e.Message} Nothing was signed.");
            return null;
        }

        // Signing says "this exact file is ours": the package in hand has to be it.
        var package = new FileInfo(packagePath);
        if (!package.Exists)
        {
            Console.Error.WriteLine($"{packagePath} does not exist. Nothing was signed.");
            return null;
        }

        await using (var stream = package.OpenRead())
        {
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            if (package.Length != manifest.Size || sha256 != manifest.Sha256)
            {
                Console.Error.WriteLine($"{package.Name} is not the file in the manifest: SHA-256 {sha256}, the manifest says {manifest.Sha256}. Nothing was signed.");
                return null;
            }
        }

        Console.WriteLine($"{manifest.Component} {manifest.Version} for {manifest.Platform} ({manifest.KeySet})");
        Console.WriteLine($"  file     {manifest.File} ({manifest.Size} bytes)");
        Console.WriteLine($"  SHA-256  {manifest.Sha256}");
        Console.WriteLine($"  commit   {manifest.Commit ?? "-"}, built {manifest.CreatedAt:yyyy-MM-dd HH:mm} UTC");
        return manifest;
    }

    private static async Task<int> WriteSignatureAsync(string manifestPath, ECDsa key, ReleaseKeySet set)
    {
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var publicKey = ReleasePublicKey.FromKey(key);
        if (!set.Holds(publicKey.KeyId))
        {
            // A signature no kiosk of this set can check is worse than none: it looks finished.
            Console.Error.WriteLine($"Key {publicKey.KeyId} is {Role(publicKey.KeyId)}; the {set.Name} kiosks carry " +
                                    $"{(set.Keys.Count == 0 ? "no key yet" : string.Join(", ", set.Keys.Select(k => k.KeyId)))}. Nothing was signed.");
            return 1;
        }

        var signature = ReleaseSignature.Sign(key, manifestBytes);
        if (!signature.IsValidFor(manifestBytes, publicKey))
        {
            Console.Error.WriteLine("The signature did not verify. Nothing was written.");
            return 1;
        }

        await File.WriteAllBytesAsync(manifestPath + ".sig", signature.ToBytes());
        Console.WriteLine($"{manifestPath}.sig: signed with {set.Name} key {publicKey.KeyId}");
        return 0;
    }

    private static async Task<int> VerifyAsync(Options options)
    {
        var manifestPath = options.Required("manifest");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var signatureBytes = await File.ReadAllBytesAsync(options.Optional("signature") ?? manifestPath + ".sig");

        // The target is whatever the manifest claims - the signature is checked before any of it is believed.
        ReleaseManifest claimed;
        try
        {
            claimed = ReleaseManifest.Parse(manifestBytes);
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine($"REFUSED (MalformedManifest): {e.Message}");
            return 1;
        }

        IReadOnlyList<ReleasePublicKey> keys;
        string keySet;
        var given = options.All("public-key").Select(path => ReleasePublicKey.FromPem(File.ReadAllText(path))).ToList();
        if (given.Count > 0)
        {
            (keys, keySet) = (given, claimed.KeySet);
            Console.WriteLine($"Keys: {string.Join(", ", keys.Select(k => k.KeyId))}");
        }
        else
        {
            var set = Set(options.Required("key-set"));
            (keys, keySet) = (set.Keys, set.Name);
            Console.WriteLine($"Keys: a {set.Name} kiosk's ({(keys.Count == 0 ? "none set yet" : string.Join(", ", keys.Select(k => k.KeyId)))})");
        }

        var expected = new ReleaseExpectation(
            claimed.Component,
            claimed.Platform,
            keySet,
            options.Optional("installed") is { } installed ? Version(installed, "--installed") : null,
            options.Optional("agent-version") is { } agent ? Version(agent, "--agent-version") : claimed.MinAgentVersion,
            options.All("withdrawn"));

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

    /// <summary>
    /// A built kiosk carries its own key set and nothing of the other (T-316):
    /// none of the other set's keys, not the other set's class, and - for a
    /// production build - not even the word "staging" in any NeoPrint assembly.
    /// </summary>
    private static int CheckBuild(Options options)
    {
        var dir = options.Required("dir");
        var own = Set(options.Required("key-set"));
        var other = own == Production ? Staging : Production;
        var assemblies = Directory.GetFiles(dir, "NeoPrint.*.dll").Order(StringComparer.Ordinal).ToList();
        var core = assemblies.FirstOrDefault(path => Path.GetFileName(path) == "NeoPrint.Kiosk.Core.dll");
        if (core is null)
        {
            Console.Error.WriteLine($"{dir} holds no NeoPrint.Kiosk.Core.dll.");
            return 1;
        }

        var problems = new List<string>();
        foreach (var path in assemblies)
        {
            var bytes = File.ReadAllBytes(path);
            var name = Path.GetFileName(path);
            if (other.Keys.Any(key => KeyLines(key).Any(line => Contains(bytes, Encoding.Unicode.GetBytes(line)))))
            {
                problems.Add($"{name} holds a {other.Name} key");
            }

            var otherClass = other == Staging ? nameof(StagingReleaseKeys) : nameof(ProductionReleaseKeys);
            if (Contains(bytes, Encoding.UTF8.GetBytes(otherClass)))
            {
                problems.Add($"{name} compiles the {other.Name} key set");
            }

            if (own == Production
                && (Contains(Lower(bytes), Encoding.UTF8.GetBytes(Staging.Name)) || Contains(Lower(bytes), Encoding.Unicode.GetBytes(Staging.Name))))
            {
                problems.Add($"{name} contains the word '{Staging.Name}'");
            }
        }

        var coreBytes = File.ReadAllBytes(core);
        if (own.Keys.Any(key => !KeyLines(key).All(line => Contains(coreBytes, Encoding.Unicode.GetBytes(line)))))
        {
            problems.Add($"NeoPrint.Kiosk.Core.dll lacks a {own.Name} key");
        }

        foreach (var problem in problems)
        {
            Console.Error.WriteLine($"FAIL: {problem}");
        }

        Console.WriteLine(problems.Count == 0
            ? $"OK: {assemblies.Count} NeoPrint assemblies carry the {own.Name} key set ({own.Keys.Count} keys) and nothing of the {other.Name} set."
            : $"{problems.Count} problem(s).");
        return problems.Count == 0 ? 0 : 1;
    }

    private static IEnumerable<string> KeyLines(ReleasePublicKey key) =>
        key.ToPem().Split('\n').Select(line => line.Trim()).Where(line => line.Length > 20 && !line.StartsWith('-'));

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    private static byte[] Lower(byte[] bytes) => bytes.Select(b => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b).ToArray();

    private static ECDsa? OpenKey(string pem, string passphrase)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromEncryptedPem(pem, passphrase);
            return key;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            key.Dispose();
            Console.Error.WriteLine("The key could not be opened: wrong passphrase, or not an encrypted private key. Nothing was signed.");
            return null;
        }
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
