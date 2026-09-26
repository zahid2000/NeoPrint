using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Storage;

/// <summary>Which S3-compatible product a provider is (plan section 2.10).</summary>
public enum StorageProviderType
{
    MinIO = 0,

    /// <summary>Scality RING S3 Connector, or Zenko CloudServer in development.</summary>
    Scality = 1,
}

/// <summary>
/// What a provider is currently used for.
/// </summary>
/// <remarks>
/// Exactly one provider is <see cref="Primary"/>. In mirror mode one more is
/// <see cref="Secondary"/>. Everything else is <see cref="Inactive"/>: still
/// configured, and still read from for objects that were written to it, but
/// receiving no new writes.
/// </remarks>
public enum StorageProviderRole
{
    Inactive = 0,
    Primary = 1,
    Secondary = 2,
}

/// <summary>Whether the provider answered the last health check.</summary>
public enum StorageHealthStatus
{
    /// <summary>Never checked, or the check has not finished yet.</summary>
    Unknown = 0,

    Healthy = 1,
    Unhealthy = 2,
}

/// <summary>The three buckets a provider holds (plan section 2.10).</summary>
public sealed class StorageBuckets
{
    /// <summary>User files, scans, layouts and their versions.</summary>
    public string Files { get; set; } = string.Empty;

    public string Receipts { get; set; } = string.Empty;

    /// <summary>Preview PDFs and conversion output, cleaned up after 24 hours.</summary>
    public string Temp { get; set; } = string.Empty;

    /// <summary>The three names, for iterating without repeating them.</summary>
    public IEnumerable<string> All()
    {
        yield return Files;
        yield return Receipts;
        yield return Temp;
    }
}

/// <summary>
/// One configured object store (plan section 2.10).
/// </summary>
/// <remarks>
/// <para>
/// The access key and secret are held encrypted with the storage secrets key
/// from OpenBao, so a dump of the database does not hand over the object
/// store. They are decrypted only when a client is built.
/// </para>
/// <para>
/// The role is changed through <see cref="MakePrimary"/> and the other
/// transition methods rather than by assignment, so the rule that there is
/// exactly one primary is enforced in one place
/// (<c>docs/STATE_MACHINES.md</c>).
/// </para>
/// </remarks>
public sealed class StorageProvider : Entity
{
    public string Name { get; set; } = string.Empty;

    public StorageProviderType Type { get; set; }

    /// <summary>The S3 endpoint, e.g. <c>http://minio:9000</c>.</summary>
    public string Endpoint { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    public bool UseSsl { get; set; }

    /// <summary>The access key, encrypted (never the plaintext).</summary>
    public string AccessKeyCiphertext { get; set; } = string.Empty;

    /// <summary>The secret key, encrypted (never the plaintext).</summary>
    public string SecretKeyCiphertext { get; set; } = string.Empty;

    public StorageBuckets Buckets { get; set; } = new();

    public StorageProviderRole Role { get; private set; } = StorageProviderRole.Inactive;

    public StorageHealthStatus HealthStatus { get; private set; } = StorageHealthStatus.Unknown;

    /// <summary>Why the last health check failed, for the panel. Never a credential.</summary>
    public string? HealthMessage { get; private set; }

    public DateTime? LastCheckedAt { get; private set; }

    /// <summary>
    /// Whether a connection test has ever passed.
    /// </summary>
    /// <remarks>
    /// A provider that has not been proven reachable cannot be given a role:
    /// making an unverified provider primary would send every upload into a
    /// void (plan section 2.10).
    /// </remarks>
    public bool IsVerified { get; private set; }

    /// <summary>The roles a provider may move between.</summary>
    public static readonly StateMachine<StorageProviderRole> Transitions =
        new StateMachine<StorageProviderRole>(nameof(StorageProvider))
            .Allow(StorageProviderRole.Inactive, StorageProviderRole.Primary, StorageProviderRole.Secondary)
            .Allow(StorageProviderRole.Primary, StorageProviderRole.Inactive, StorageProviderRole.Secondary)
            .Allow(StorageProviderRole.Secondary, StorageProviderRole.Inactive, StorageProviderRole.Primary);

    /// <summary>Records the result of a connection test or health check.</summary>
    public void RecordHealth(bool healthy, string? message, DateTime now)
    {
        HealthStatus = healthy ? StorageHealthStatus.Healthy : StorageHealthStatus.Unhealthy;
        HealthMessage = healthy ? null : message;
        LastCheckedAt = now;

        if (healthy)
        {
            IsVerified = true;
        }
    }

    /// <summary>Makes this provider the one new objects are written to.</summary>
    /// <exception cref="DomainException">It has never passed a connection test.</exception>
    public void MakePrimary(ActorContext actor, DateTime now)
        => ChangeRole(StorageProviderRole.Primary, actor, now);

    /// <summary>Makes this provider the mirror target.</summary>
    public void MakeSecondary(ActorContext actor, DateTime now)
        => ChangeRole(StorageProviderRole.Secondary, actor, now);

    /// <summary>
    /// Takes the provider out of the write path.
    /// </summary>
    /// <remarks>
    /// Objects already recorded against it stay readable: a file stores the
    /// provider it was written to, and reads follow that (plan section 2.10).
    /// </remarks>
    public void Deactivate(ActorContext actor, DateTime now)
        => ChangeRole(StorageProviderRole.Inactive, actor, now);

    private void ChangeRole(StorageProviderRole next, ActorContext actor, DateTime now)
    {
        if (next != StorageProviderRole.Inactive && !IsVerified)
        {
            throw new DomainException(
                $"'{Name}' has not passed a connection test, so it cannot be given a role. "
                + "Run the connection test first (plan section 2.10).");
        }

        Record(Transitions.Transition(Id, Role, next, actor, now));
        Role = next;
    }
}
