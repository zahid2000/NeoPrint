using Amazon.Runtime;
using Amazon.S3;

using NeoPrint.Domain.Storage;

namespace NeoPrint.Infrastructure.Storage;

/// <summary>
/// Builds an S3 client for a configured provider.
/// </summary>
/// <remarks>
/// <para>
/// MinIO and Scality are both reached with the same client
/// (<c>ForcePathStyle = true</c>, plan section 2.10); only the endpoint and
/// the credentials differ. Where the two products genuinely differ - health
/// endpoints, checksum headers - the difference belongs in the code that
/// needs it, not in a second client.
/// </para>
/// <para>
/// Clients are cached per provider and rebuilt when the provider's document
/// changes. AmazonS3Client holds a connection pool, so building one per
/// request would leak sockets under load.
/// </para>
/// </remarks>
public sealed class S3ClientFactory : IDisposable
{
    private readonly StorageSecretProtector _secrets;

    // System.Threading.Lock is .NET 9; this targets .NET 8.
    private readonly object _gate = new();

    /// <summary>Cached clients, keyed by provider and the version they were built from.</summary>
    private readonly Dictionary<Guid, (int RowVersion, AmazonS3Client Client)> _clients = [];

    public S3ClientFactory(StorageSecretProtector secrets) => _secrets = secrets;

    /// <summary>
    /// A client for this provider, built from its current credentials.
    /// </summary>
    /// <remarks>
    /// The cache key includes the row version, so editing a provider's
    /// endpoint or credentials takes effect on the next call rather than
    /// leaving a client pointed at the old address.
    /// </remarks>
    public AmazonS3Client For(StorageProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (_gate)
        {
            if (_clients.TryGetValue(provider.Id, out var cached))
            {
                if (cached.RowVersion == provider.RowVersion)
                {
                    return cached.Client;
                }

                cached.Client.Dispose();
                _clients.Remove(provider.Id);
            }

            var client = Build(provider);
            _clients[provider.Id] = (provider.RowVersion, client);

            return client;
        }
    }

    /// <summary>
    /// A client built from credentials that are not stored yet.
    /// </summary>
    /// <remarks>
    /// For the connection test on the add-provider form: the credentials have
    /// to be proven before they are saved, so there is nothing to decrypt yet.
    /// Not cached - the provider may never be saved at all.
    /// </remarks>
    public static AmazonS3Client ForUnsaved(
        string endpoint, string region, bool useSsl, string accessKey, string secretKey)
    {
        return new AmazonS3Client(
            new BasicAWSCredentials(accessKey, secretKey),
            Configure(endpoint, region, useSsl));
    }

    private AmazonS3Client Build(StorageProvider provider)
    {
        var accessKey = _secrets.Unprotect(provider.AccessKeyCiphertext, provider.Id);
        var secretKey = _secrets.Unprotect(provider.SecretKeyCiphertext, provider.Id);

        return new AmazonS3Client(
            new BasicAWSCredentials(accessKey, secretKey),
            Configure(provider.Endpoint, provider.Region, provider.UseSsl));
    }

    private static AmazonS3Config Configure(string endpoint, string region, bool useSsl)
    {
        return new AmazonS3Config
        {
            ServiceURL = endpoint,

            // Neither MinIO nor Scality serves buckets as subdomains of the
            // endpoint, so virtual-host addressing would resolve to nothing.
            ForcePathStyle = true,

            AuthenticationRegion = string.IsNullOrWhiteSpace(region) ? "us-east-1" : region,
            UseHttp = !useSsl,

            // A provider that does not answer is given up on in seconds, not
            // minutes: in mirror mode the secondary is waiting to serve the
            // read, and a user is waiting on the other end of it.
            MaxErrorRetry = 2,
            Timeout = TimeSpan.FromSeconds(30),

            // The object is already encrypted and its hash is recorded, so the
            // SDK's own integrity headers add a second checksum that some
            // S3-compatible servers reject outright.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var (_, client) in _clients.Values)
            {
                client.Dispose();
            }

            _clients.Clear();
        }
    }
}
