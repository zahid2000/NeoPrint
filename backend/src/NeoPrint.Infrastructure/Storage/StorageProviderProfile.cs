using NeoPrint.Domain.Storage;

namespace NeoPrint.Infrastructure.Storage;

/// <summary>
/// Where the S3-compatible products differ, kept in one place
/// (plan section 2.10).
/// </summary>
/// <remarks>
/// <para>
/// Every read and write goes through the S3 API alone - the same client, the
/// same calls - so a provider can be swapped or migrated to another without
/// the rest of the code knowing. What is left over is small and lives here:
/// today, only how a product reports that it is ready to serve.
/// </para>
/// <para>
/// Nothing here may be needed for correctness. A product this class knows
/// nothing about still works; it just gets the plain S3 health check.
/// </para>
/// </remarks>
public static class StorageProviderProfile
{
    /// <summary>
    /// An unauthenticated readiness endpoint, relative to the S3 endpoint, or
    /// null when the product has none this code relies on.
    /// </summary>
    /// <remarks>
    /// Asked before the signed S3 check, because it answers a question that
    /// check cannot: RustFS and MinIO report "not ready" while their disks are
    /// missing or still loading, when a bucket lookup may yet succeed from
    /// cached metadata. Scality's S3 Connector has no equivalent the RING
    /// guarantees, so it relies on the S3 check alone.
    /// </remarks>
    public static string? ReadinessPath(StorageProviderType type) => type switch
    {
        StorageProviderType.RustFS => "/health/ready",
        StorageProviderType.MinIO => "/minio/health/ready",
        _ => null,
    };

    /// <summary>The readiness URL for a provider, or null.</summary>
    public static Uri? ReadinessUri(StorageProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var path = ReadinessPath(provider.Type);

        if (path is null || !Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint))
        {
            return null;
        }

        return new Uri(endpoint, path);
    }
}
