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
