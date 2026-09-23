namespace NeoPrint.Infrastructure.SecretStore;

/// <summary>
/// How to reach OpenBao. These are the only settings that come from the
/// environment; every actual secret is read from OpenBao itself
/// (plan section 2.13).
/// </summary>
public sealed class OpenBaoOptions
{
    /// <summary>The KV v2 paths loaded into configuration, in order.</summary>
    public static readonly string[] DefaultPaths =
    [
        "backend/core",
        "backend/crypto",
        "backend/providers",
        "backend/seed",
    ];

    /// <summary>Base address, for example <c>https://bao.neoprint.az:8200</c>.</summary>
    public string Address { get; set; } = "http://localhost:8200";

    /// <summary>AppRole role id, or <see langword="null"/> when it is read from <see cref="RoleIdFile"/>.</summary>
    public string? RoleId { get; set; }

    /// <summary>
    /// File holding the AppRole role id. Preferred over <see cref="RoleId"/>:
    /// in production the credentials arrive as Docker secrets, not as
    /// environment variables.
    /// </summary>
    public string? RoleIdFile { get; set; }

    /// <summary>AppRole secret id, or <see langword="null"/> when it is read from <see cref="SecretIdFile"/>.</summary>
    public string? SecretId { get; set; }

    /// <summary>File holding the AppRole secret id.</summary>
    public string? SecretIdFile { get; set; }

    /// <summary>KV v2 mount name.</summary>
    public string KvMount { get; set; } = "neoprint";

    /// <summary>Paths to load, relative to the mount.</summary>
    public IList<string> Paths { get; set; } = [.. DefaultPaths];

    /// <summary>
    /// How often the values are re-read, so rotated secrets (SMTP, Telegram,
    /// provider keys) take effect without a restart (plan section 2.13).
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long startup keeps retrying before giving up. Plan section 2.13
    /// sets this at 60 seconds.
    /// </summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// First wait between startup attempts; it doubles up to
    /// <see cref="MaxRetryDelay"/>.
    /// </summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound for the startup backoff.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>CA certificate for the production TLS listener.</summary>
    public string? CaCertificatePath { get; set; }

    /// <summary>
    /// When true, the application starts even if OpenBao cannot be reached.
    /// Only ever set outside production - the whole point of the secret store
    /// is that production does not start without it.
    /// </summary>
    public bool Optional { get; set; }

    /// <summary>
    /// Reads the bootstrap settings from the environment variables documented
    /// in docs/OPENBAO.md.
    /// </summary>
    public static OpenBaoOptions FromEnvironment(IDictionary<string, string?>? environment = null)
    {
        string? Get(string name) => environment is null
            ? Environment.GetEnvironmentVariable(name)
            : environment.TryGetValue(name, out var value) ? value : null;

        var options = new OpenBaoOptions
        {
            RoleId = Get("BAO_ROLE_ID"),
            RoleIdFile = Get("BAO_ROLE_ID_FILE"),
            SecretId = Get("BAO_SECRET_ID"),
            SecretIdFile = Get("BAO_SECRET_ID_FILE"),
            CaCertificatePath = Get("BAO_CACERT"),
        };

        var address = Get("BAO_ADDR");
        if (!string.IsNullOrWhiteSpace(address))
        {
            options.Address = address;
        }

        var mount = Get("BAO_KV_MOUNT");
        if (!string.IsNullOrWhiteSpace(mount))
        {
            options.KvMount = mount;
        }

        return options;
    }

    /// <summary>
    /// Resolves the AppRole credentials, reading them from their files when
    /// file paths were given.
    /// </summary>
    /// <exception cref="SecretStoreConfigurationException">
    /// Neither a value nor a readable file was supplied.
    /// </exception>
    public (string RoleId, string SecretId) ResolveCredentials(Func<string, string>? readFile = null)
    {
        readFile ??= File.ReadAllText;

        var roleId = Resolve(RoleId, RoleIdFile, "BAO_ROLE_ID", "BAO_ROLE_ID_FILE", readFile);
        var secretId = Resolve(SecretId, SecretIdFile, "BAO_SECRET_ID", "BAO_SECRET_ID_FILE", readFile);

        return (roleId, secretId);
    }

    private static string Resolve(
        string? value,
        string? path,
        string valueVariable,
        string fileVariable,
        Func<string, string> readFile)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new SecretStoreConfigurationException(
                $"Neither {valueVariable} nor {fileVariable} is set. The application cannot authenticate to OpenBao.");
        }

        string contents;
        try
        {
            contents = readFile(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The path is safe to log; the contents are not.
            throw new SecretStoreConfigurationException(
                $"{fileVariable} points at '{path}', which could not be read: {exception.Message}", exception);
        }

        if (string.IsNullOrWhiteSpace(contents))
        {
            throw new SecretStoreConfigurationException($"{fileVariable} points at '{path}', which is empty.");
        }

        return contents.Trim();
    }

    /// <summary>Validates the settings themselves, before any network call.</summary>
    /// <exception cref="SecretStoreConfigurationException">A setting is missing or nonsensical.</exception>
    public void Validate()
    {
        if (!Uri.TryCreate(Address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new SecretStoreConfigurationException(
                $"BAO_ADDR must be an absolute http or https URL, but was '{Address}'.");
        }

        if (string.IsNullOrWhiteSpace(KvMount))
        {
            throw new SecretStoreConfigurationException("BAO_KV_MOUNT must not be empty.");
        }

        if (Paths.Count == 0)
        {
            throw new SecretStoreConfigurationException("At least one OpenBao path must be configured.");
        }

        if (RefreshInterval <= TimeSpan.Zero)
        {
            throw new SecretStoreConfigurationException("The OpenBao refresh interval must be positive.");
        }
    }
}
