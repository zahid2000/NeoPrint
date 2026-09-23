namespace NeoPrint.Infrastructure.SecretStore;

/// <summary>
/// The secrets the backend cannot run without.
/// </summary>
/// <remarks>
/// Plan section 2.13: if a mandatory key is absent from OpenBao the application
/// does not start, and logs which key is missing - never its value. Optional
/// keys (FCM, Telegram, the real fiscal and bank providers) are deliberately
/// not listed: their absence disables a feature rather than stopping the
/// service.
/// </remarks>
public static class RequiredSecrets
{
    /// <summary>Keys that must be present and non-empty.</summary>
    public static readonly string[] Keys =
    [
        // neoprint/backend/core
        "NEOPRINT_MONGO_URI",
        "NEOPRINT_REDIS",
        "NEOPRINT_RABBITMQ_URI",
        "NEOPRINT_JWT_SIGNING_KEY",

        // neoprint/backend/crypto
        "NEOPRINT_KEK_CURRENT_ID",
        "NEOPRINT_STORAGE_SECRETS_KEY",
        "NEOPRINT_DATA_PROTECTION_KEY",
        "NEOPRINT_QR_HMAC_KEY",
    ];

    /// <summary>Prefix of the key encryption keys, one per key id.</summary>
    public const string KeyEncryptionKeyPrefix = "NEOPRINT_KEK__";

    /// <summary>Names the current key encryption key.</summary>
    public const string CurrentKeyEncryptionKeyId = "NEOPRINT_KEK_CURRENT_ID";

    /// <summary>
    /// Returns the mandatory keys that are missing or empty.
    /// </summary>
    /// <remarks>
    /// The key encryption key named by <c>NEOPRINT_KEK_CURRENT_ID</c> is checked
    /// as well: a current id pointing at a key that was never written would
    /// otherwise only fail when the first file is uploaded.
    /// </remarks>
    public static IReadOnlyCollection<string> FindMissing(IReadOnlyDictionary<string, string?> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        var missing = new List<string>();

        foreach (var key in Keys)
        {
            if (!secrets.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                missing.Add(key);
            }
        }

        if (secrets.TryGetValue(CurrentKeyEncryptionKeyId, out var currentId) && !string.IsNullOrWhiteSpace(currentId))
        {
            var expected = KeyEncryptionKeyPrefix + currentId.Trim();
            if (!secrets.TryGetValue(expected, out var kek) || string.IsNullOrWhiteSpace(kek))
            {
                missing.Add(expected);
            }
        }

        return missing;
    }
}
