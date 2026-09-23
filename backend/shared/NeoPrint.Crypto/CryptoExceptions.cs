namespace NeoPrint.Crypto;

/// <summary>
/// Base type for every failure raised by this library, so a caller can tell a
/// cryptographic problem from an I/O problem.
/// </summary>
public abstract class NeoPrintCryptoException : Exception
{
    protected NeoPrintCryptoException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The data is not a NeoPrint encrypted object, or its header is malformed or
/// truncated.
/// </summary>
public sealed class CryptoFormatException : NeoPrintCryptoException
{
    public CryptoFormatException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The object references a key encryption key that is not available.
/// </summary>
/// <remarks>
/// In practice this means a key was removed from OpenBao before every object
/// wrapped with it had been rewrapped (plan section 2.13).
/// </remarks>
public sealed class UnknownKeyException : NeoPrintCryptoException
{
    public UnknownKeyException(string kekId)
        : base($"The object is wrapped with key encryption key '{kekId}', which is not available. " +
               "The key must stay in the secret store until every object has been rewrapped.")
    {
        KekId = kekId;
    }

    public string KekId { get; }
}

/// <summary>
/// Authentication failed: the content was modified, truncated, reordered, or
/// the wrong key was used.
/// </summary>
/// <remarks>
/// This is deliberately a single exception type with a single message. Telling
/// "wrong key" apart from "tampered content" in the error would leak
/// information to an attacker.
/// </remarks>
public sealed class CryptoAuthenticationException : NeoPrintCryptoException
{
    public CryptoAuthenticationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
