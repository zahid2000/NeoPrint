using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

using NeoPrint.Crypto;

namespace NeoPrint.Infrastructure.Crypto;

/// <summary>
/// Builds the key ring from the <c>NEOPRINT_KEK__*</c> values the secret store
/// supplies, and rebuilds it when they change.
/// </summary>
/// <remarks>
/// Plan section 2.13: rotating a key means writing a new
/// <c>NEOPRINT_KEK__{id}</c> and moving <c>NEOPRINT_KEK_CURRENT_ID</c> to it.
/// The configuration provider notices within five minutes, this provider
/// rebuilds the ring, and new objects are wrapped with the new key - no
/// restart, and older objects stay readable because the retired keys are still
/// in the ring.
/// </remarks>
public sealed class ConfigurationKeyRingProvider : IKeyEncryptionKeyProvider, IDisposable
{
    private const string KeyPrefix = "NEOPRINT_KEK__";
    private const string CurrentKeyIdName = "NEOPRINT_KEK_CURRENT_ID";

    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigurationKeyRingProvider> _logger;
    private readonly IDisposable? _changeSubscription;
    // System.Threading.Lock is .NET 9; this targets .NET 8.
    private readonly object _gate = new();

    private KeyRing _ring;

    public ConfigurationKeyRingProvider(IConfiguration configuration, ILogger<ConfigurationKeyRingProvider> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _ring = Build();

        _changeSubscription = ChangeToken.OnChange(_configuration.GetReloadToken, Reload);
    }

    /// <inheritdoc />
    public KeyEncryptionKey Current
    {
        get
        {
            lock (_gate)
            {
                return _ring.Current;
            }
        }
    }

    /// <inheritdoc />
    public bool TryGet(string kekId, [NotNullWhen(true)] out KeyEncryptionKey? key)
    {
        lock (_gate)
        {
            return _ring.TryGet(kekId, out key);
        }
    }

    private void Reload()
    {
        try
        {
            var rebuilt = Build();

            lock (_gate)
            {
                if (string.Equals(rebuilt.Current.Id, _ring.Current.Id, StringComparison.Ordinal) &&
                    rebuilt.KeyIds.Count == _ring.KeyIds.Count)
                {
                    return;
                }

                _ring = rebuilt;
            }

            _logger.LogInformation(
                "The key encryption key ring was reloaded; the active key is now '{KeyId}'.", rebuilt.Current.Id);
        }
        catch (Exception exception)
        {
            // Keep the ring we have: a bad reload must not make every stored
            // object unreadable.
            _logger.LogError(exception, "Rebuilding the key ring failed; keeping the previous keys.");
        }
    }

    private KeyRing Build()
    {
        var currentId = _configuration[CurrentKeyIdName];

        if (string.IsNullOrWhiteSpace(currentId))
        {
            throw new InvalidOperationException(
                $"'{CurrentKeyIdName}' is not configured. It names the key encryption key new objects are wrapped with (docs/OPENBAO.md).");
        }

        currentId = currentId.Trim();

        var keys = new List<KeyEncryptionKey>();

        foreach (var entry in _configuration.AsEnumerable())
        {
            if (entry.Key is null || !entry.Key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = entry.Key[KeyPrefix.Length..];
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(entry.Value))
            {
                continue;
            }

            keys.Add(KeyEncryptionKey.FromBase64(id, entry.Value));
        }

        if (keys.Count == 0)
        {
            throw new InvalidOperationException(
                $"No '{KeyPrefix}*' key encryption keys are configured; stored objects cannot be encrypted or read.");
        }

        return new KeyRing(keys, currentId);
    }

    public void Dispose() => _changeSubscription?.Dispose();
}
