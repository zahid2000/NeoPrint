using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Localization;
using NeoPrint.Application.Storage;

namespace NeoPrint.Application.Dashboard;

/// <summary>
/// What the file store holds, for the dashboard (plan section 8A.3).
/// </summary>
/// <param name="Count">Files not deleted.</param>
/// <param name="TotalBytes">Their size as users see it (the originals, before encryption).</param>
/// <param name="InProgress">Files waiting for or in conversion.</param>
/// <param name="FailedLast24Hours">Conversions that failed in the last 24 hours.</param>
/// <param name="FailedLast7Days">Conversions that failed in the last 7 days.</param>
public sealed record FileStatistics(
    long Count,
    long TotalBytes,
    long InProgress,
    long FailedLast24Hours,
    long FailedLast7Days);

/// <summary>Reads the file figures; implemented over MongoDB.</summary>
public interface IFileStatistics
{
    Task<FileStatistics> GetAsync(DateTime now, CancellationToken cancellationToken = default);
}

/// <summary>A storage provider as the dashboard shows it: no endpoint, no key.</summary>
public sealed record DashboardStorageProvider(
    Guid Id,
    string Name,
    string Type,
    string Role,
    string HealthStatus,
    string? HealthMessage,
    DateTime? LastCheckedAt);

/// <summary>The storage cards: provider health and the sync that is running, if any.</summary>
/// <param name="ActiveSync">A job that is pending, running or paused.</param>
/// <param name="LastSync">The most recent job that finished, for when none is running.</param>
public sealed record DashboardStorage(
    IReadOnlyList<DashboardStorageProvider> Providers,
    StorageSyncJobResponse? ActiveSync,
    StorageSyncJobResponse? LastSync);

/// <summary>
/// The dashboard (plan section 6: <c>GET admin/dashboard</c>). It fills up step
/// by step as the features it reports on arrive; step 2 brings storage and files.
/// </summary>
/// <param name="Storage">Present only for accounts that may manage storage.</param>
public sealed record AdminDashboardResponse(FileStatistics Files, DashboardStorage? Storage);

/// <param name="IncludeStorage">
/// Whether the caller holds <c>storage.manage</c>. The Admin role template
/// deliberately has no storage access (plan section 8A.2), so the cards are
/// left out rather than shown to everyone with <c>dashboard.view</c>.
/// </param>
public sealed record GetAdminDashboardQuery(bool IncludeStorage) : IRequest<AdminDashboardResponse>;

public sealed class GetAdminDashboardHandler : IRequestHandler<GetAdminDashboardQuery, AdminDashboardResponse>
{
    private static readonly HashSet<string> ActiveStatuses = new(StringComparer.Ordinal) { "Pending", "Running", "Paused", "Verifying" };

    private readonly IFileStatistics _files;
    private readonly IStorageAdministration _storage;
    private readonly IClock _clock;

    public GetAdminDashboardHandler(IFileStatistics files, IStorageAdministration storage, IClock clock)
    {
        _files = files;
        _storage = storage;
        _clock = clock;
    }

    public async Task<AdminDashboardResponse> Handle(GetAdminDashboardQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var files = await _files.GetAsync(_clock.UtcNow, cancellationToken).ConfigureAwait(false);

        if (!request.IncludeStorage)
        {
            return new AdminDashboardResponse(files, null);
        }

        var providers = await _storage.ListProvidersAsync(cancellationToken).ConfigureAwait(false);
        var jobs = await _storage.ListJobsAsync(cancellationToken).ConfigureAwait(false);

        var storage = new DashboardStorage(
            providers
                .Select(provider => new DashboardStorageProvider(
                    provider.Id,
                    provider.Name,
                    provider.Type,
                    provider.Role,
                    provider.HealthStatus,
                    Messages.WriteStored(provider.HealthMessage),
                    provider.LastCheckedAt))
                .ToList(),
            jobs.Where(job => ActiveStatuses.Contains(job.Status)).MaxBy(job => job.StartedAt ?? DateTime.MaxValue),
            jobs.Where(job => !ActiveStatuses.Contains(job.Status)).MaxBy(job => job.FinishedAt ?? DateTime.MinValue));

        return new AdminDashboardResponse(files, storage);
    }
}
