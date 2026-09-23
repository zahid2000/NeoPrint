namespace NeoPrint.Infrastructure.SecretStore;

/// <summary>
/// Base type for secret store failures.
/// </summary>
/// <remarks>
/// No exception in this file ever carries a secret value. Messages name keys,
/// paths and status codes only (plan section 2.13: secrets, tokens and personal
/// data are never written to the log).
/// </remarks>
public abstract class SecretStoreException : Exception
{
    protected SecretStoreException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The bootstrap settings are missing or invalid.</summary>
public sealed class SecretStoreConfigurationException : SecretStoreException
{
    public SecretStoreConfigurationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>OpenBao could not be reached, or refused the credentials.</summary>
public sealed class SecretStoreUnavailableException : SecretStoreException
{
    public SecretStoreUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A secret the application cannot run without is absent from the store.
/// </summary>
public sealed class MissingSecretsException : SecretStoreException
{
    public MissingSecretsException(IReadOnlyCollection<string> missingKeys)
        : base(BuildMessage(missingKeys))
    {
        MissingKeys = missingKeys;
    }

    public IReadOnlyCollection<string> MissingKeys { get; }

    private static string BuildMessage(IReadOnlyCollection<string> missingKeys)
        => "The following mandatory secrets are missing from OpenBao: " +
           string.Join(", ", missingKeys.Order(StringComparer.Ordinal)) +
           ". Write them with `bao kv put` and restart (docs/OPENBAO.md).";
}
