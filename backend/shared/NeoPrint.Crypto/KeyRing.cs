using System.Diagnostics.CodeAnalysis;

namespace NeoPrint.Crypto;

/// <summary>
/// An immutable set of key encryption keys with one of them marked current.
/// </summary>
/// <remarks>
/// The backend rebuilds a key ring whenever the configuration provider picks up
/// a change in OpenBao, so a rotation takes effect without a restart
/// (plan section 2.13).
/// </remarks>
public sealed class KeyRing : IKeyEncryptionKeyProvider
{
    private readonly Dictionary<string, KeyEncryptionKey> _keys;

    /// <param name="keys">Every key that must stay resolvable, current and retired alike.</param>
    /// <param name="currentKeyId">The id of the key new objects are wrapped with.</param>
    public KeyRing(IEnumerable<KeyEncryptionKey> keys, string currentKeyId)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentKeyId);

        _keys = keys.ToDictionary(key => key.Id, StringComparer.Ordinal);

        if (_keys.Count == 0)
        {
            throw new ArgumentException("A key ring needs at least one key.", nameof(keys));
        }

        if (!_keys.TryGetValue(currentKeyId, out var current))
        {
            throw new ArgumentException(
                $"The current key id '{currentKeyId}' is not among the supplied keys. " +
                $"Add NEOPRINT_KEK__{currentKeyId} before pointing NEOPRINT_KEK_CURRENT_ID at it.",
                nameof(currentKeyId));
        }

        Current = current;
    }

    /// <inheritdoc />
    public KeyEncryptionKey Current { get; }

    /// <summary>The ids of every key in the ring.</summary>
    public IReadOnlyCollection<string> KeyIds => _keys.Keys;

    /// <inheritdoc />
    public bool TryGet(string kekId, [NotNullWhen(true)] out KeyEncryptionKey? key)
        => _keys.TryGetValue(kekId, out key);
}
