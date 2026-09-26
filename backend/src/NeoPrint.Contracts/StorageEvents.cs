namespace NeoPrint.Contracts;

/// <summary>
/// The storage configuration changed: a provider was added, edited or given
/// another role, or a sync job started or finished.
/// </summary>
/// <remarks>
/// Every API and worker process caches the providers, so each has to drop its
/// cache when this arrives; that is how a change made on one instance takes
/// effect on all of them without a restart (plan section 2.10).
/// </remarks>
/// <param name="ProviderId">The provider concerned, when there is one.</param>
public sealed record StorageConfigChanged(Guid? ProviderId);

/// <summary>
/// An object was written to the primary and should be repeated on the
/// secondary (plan section 2.10, mirror mode).
/// </summary>
/// <remarks>
/// Published after the write succeeded, so the upload does not wait for the
/// mirror; a failed copy is retried by the bus and, failing that, caught by
/// the nightly reconciliation.
/// </remarks>
/// <param name="ProviderId">The provider the object was written to.</param>
/// <param name="Bucket">The bucket role: Files, Receipts or Temp.</param>
/// <param name="Key">The object key.</param>
public sealed record ObjectStored(Guid ProviderId, string Bucket, string Key);
