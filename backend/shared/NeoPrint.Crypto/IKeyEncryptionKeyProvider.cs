using System.Diagnostics.CodeAnalysis;

namespace NeoPrint.Crypto;

/// <summary>
/// Supplies the key encryption keys. The implementation in the backend reads
/// them from OpenBao and refreshes them without a restart; the kiosk uses a
/// local, machine-protected key for its spool.
/// </summary>
public interface IKeyEncryptionKeyProvider
{
    /// <summary>
    /// The key new objects are wrapped with, that is
    /// <c>NEOPRINT_KEK__{NEOPRINT_KEK_CURRENT_ID}</c>.
    /// </summary>
    KeyEncryptionKey Current { get; }

    /// <summary>
    /// Looks up a key by the id stored in an object header. Older keys have to
    /// remain resolvable so that objects written before a rotation can still be
    /// read.
    /// </summary>
    bool TryGet(string kekId, [NotNullWhen(true)] out KeyEncryptionKey? key);
}
