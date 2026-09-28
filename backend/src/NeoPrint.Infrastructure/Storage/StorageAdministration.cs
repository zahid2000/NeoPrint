using Hangfire;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Localization;
using NeoPrint.Application.Storage;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Storage;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Storage;

/// <inheritdoc cref="IStorageAdministration" />
public sealed class StorageAdministration : IStorageAdministration
{
    private readonly MongoContext _context;
    private readonly StorageProviderRegistry _registry;
    private readonly StorageConnectionTester _tester;
    private readonly StorageSecretProtector _secrets;
    private readonly StorageSyncScheduler _scheduler;
    private readonly IBackgroundJobClient _jobs;
    private readonly IEventPublisher _events;
    private readonly ICurrentUser _currentUser;
    private readonly ISettingsReader _settings;
    private readonly IClock _clock;
    private readonly ILogger<StorageAdministration> _logger;

    public StorageAdministration(
        MongoContext context,
        StorageProviderRegistry registry,
        StorageConnectionTester tester,
        StorageSecretProtector secrets,
        StorageSyncScheduler scheduler,
        IBackgroundJobClient jobs,
        IEventPublisher events,
        ICurrentUser currentUser,
        ISettingsReader settings,
        IClock clock,
        ILogger<StorageAdministration> logger)
    {
        _context = context;
        _registry = registry;
        _tester = tester;
        _secrets = secrets;
        _scheduler = scheduler;
        _jobs = jobs;
        _events = events;
        _currentUser = currentUser;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    private ActorContext Actor(string reason)
        => _currentUser.AdminUserId is { } id
            ? ActorContext.Admin(id, reason)
            : ActorContext.System(reason);

    // -----------------------------------------------------------------------
    // Providers
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<StorageProviderResponse>> ListProvidersAsync(CancellationToken cancellationToken)
    {
        var providers = await _context.StorageProviders
            .Find(FilterDefinition<StorageProvider>.Empty)
            .SortBy(p => p.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. providers.Select(Map)];
    }

    public async Task<StorageTestResponse> TestAsync(StorageProviderInput input, CancellationToken cancellationToken)
    {
        Validate(input, requireSecret: true);

        var result = await _tester.TestAsync(
                input.Endpoint.Trim(),
                Region(input),
                input.UseSsl,
                input.AccessKey.Trim(),
                input.SecretKey!,
                Buckets(input),
                cancellationToken)
            .ConfigureAwait(false);

        return Map(result);
    }

    public async Task<StorageTestResponse> TestAsync(Guid providerId, CancellationToken cancellationToken)
    {
        var provider = await LoadAsync(providerId, cancellationToken).ConfigureAwait(false);
        var result = await _tester.TestAsync(provider, cancellationToken).ConfigureAwait(false);

        provider.RecordHealth(result.Succeeded, result.Succeeded ? null : result.Message, _clock.UtcNow);
        await SaveAsync(provider, cancellationToken).ConfigureAwait(false);

        return Map(result);
    }

    public async Task<StorageProviderResponse> AddProviderAsync(StorageProviderInput input, CancellationToken cancellationToken)
    {
        Validate(input, requireSecret: true);

        var test = await _tester.TestAsync(
                input.Endpoint.Trim(), Region(input), input.UseSsl,
                input.AccessKey.Trim(), input.SecretKey!, Buckets(input), cancellationToken)
            .ConfigureAwait(false);

        if (!test.Succeeded)
        {
            // Plan section 2.10: no activation without a passing test. Saving
            // it anyway would leave a provider nobody can give a role to.
            throw Invalid("endpoint", "storage.testFailed", ("problemMessage", test.Message));
        }

        await EnsureUniqueNameAsync(input.Name, exceptId: null, cancellationToken).ConfigureAwait(false);

        var provider = new StorageProvider
        {
            Id = Guid.NewGuid(),
            Name = input.Name.Trim(),
            Type = ParseType(input.Type),
            Endpoint = input.Endpoint.Trim(),
            Region = Region(input),
            UseSsl = input.UseSsl,
            Buckets = Buckets(input),
        };

        provider.AccessKeyCiphertext = _secrets.Protect(input.AccessKey.Trim(), provider.Id);
        provider.SecretKeyCiphertext = _secrets.Protect(input.SecretKey!, provider.Id);
        provider.RecordHealth(true, null, _clock.UtcNow);

        await AggregateWriter.InsertAsync(_context, _context.StorageProviders, provider, cancellationToken)
            .ConfigureAwait(false);

        await ChangedAsync(provider.Id, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Storage provider {Name} ({Type}) was added.", provider.Name, provider.Type);

        return Map(provider);
    }

    public async Task<StorageProviderResponse> UpdateProviderAsync(
        Guid providerId, StorageProviderInput input, CancellationToken cancellationToken)
    {
        Validate(input, requireSecret: false);

        var provider = await LoadAsync(providerId, cancellationToken).ConfigureAwait(false);

        await EnsureUniqueNameAsync(input.Name, providerId, cancellationToken).ConfigureAwait(false);

        // An empty secret means "keep the stored one": the form never receives
        // it, so it cannot send it back.
        var secret = string.IsNullOrEmpty(input.SecretKey)
            ? _secrets.Unprotect(provider.SecretKeyCiphertext, provider.Id)
            : input.SecretKey;

        var access = string.IsNullOrWhiteSpace(input.AccessKey)
            ? _secrets.Unprotect(provider.AccessKeyCiphertext, provider.Id)
            : input.AccessKey.Trim();

        var test = await _tester.TestAsync(
                input.Endpoint.Trim(), Region(input), input.UseSsl,
                access, secret, Buckets(input), cancellationToken)
            .ConfigureAwait(false);

        // A provider in use must keep working after the edit; an inactive one
        // may be saved broken, to be fixed later.
        if (!test.Succeeded && provider.Role != StorageProviderRole.Inactive)
        {
            throw Invalid(
                "endpoint",
                "storage.testFailedInUse",
                ("problemMessage", test.Message));
        }

        provider.Name = input.Name.Trim();
        provider.Type = ParseType(input.Type);
        provider.Endpoint = input.Endpoint.Trim();
        provider.Region = Region(input);
        provider.UseSsl = input.UseSsl;
        provider.Buckets = Buckets(input);
        provider.AccessKeyCiphertext = _secrets.Protect(access, provider.Id);
        provider.SecretKeyCiphertext = _secrets.Protect(secret, provider.Id);
        provider.RecordHealth(test.Succeeded, test.Succeeded ? null : test.Message, _clock.UtcNow);

        await SaveAsync(provider, cancellationToken).ConfigureAwait(false);
        await ChangedAsync(provider.Id, cancellationToken).ConfigureAwait(false);

        return Map(provider);
    }

    public async Task<StorageProviderResponse> SetRoleAsync(Guid providerId, string role, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<StorageProviderRole>(role, ignoreCase: true, out var next))
        {
            throw Invalid("role", "storage.roleInvalid");
        }

        if (await ActiveJobAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            // A migration relies on who is primary for its dual-write and its
            // switch; changing that under it would split the writes.
            throw Invalid("role", "storage.roleBusy");
        }

        var provider = await LoadAsync(providerId, cancellationToken).ConfigureAwait(false);
        var actor = Actor(UserMessage.Of("storage.reason.role", ("role", next)).Encode());

        if (next == StorageProviderRole.Inactive && provider.Role == StorageProviderRole.Primary)
        {
            throw Invalid("role", "storage.primaryOff");
        }

        if (next != StorageProviderRole.Inactive)
        {
            // Whoever holds the role steps out of it first, so there is never
            // a moment with two primaries or two secondaries.
            var holders = await _context.StorageProviders
                .Find(p => p.Role == next && p.Id != providerId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var holder in holders)
            {
                holder.Deactivate(actor, _clock.UtcNow);
                await SaveAsync(holder, cancellationToken).ConfigureAwait(false);
            }
        }

        switch (next)
        {
            case StorageProviderRole.Primary:
                provider.MakePrimary(actor, _clock.UtcNow);
                break;
            case StorageProviderRole.Secondary:
                provider.MakeSecondary(actor, _clock.UtcNow);
                break;
            default:
                provider.Deactivate(actor, _clock.UtcNow);
                break;
        }

        await SaveAsync(provider, cancellationToken).ConfigureAwait(false);
        await ChangedAsync(provider.Id, cancellationToken).ConfigureAwait(false);

        return Map(provider);
    }

    // -----------------------------------------------------------------------
    // Sync jobs
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<StorageSyncJobResponse>> ListJobsAsync(CancellationToken cancellationToken)
    {
        var jobs = await _context.StorageSyncJobs
            .Find(FilterDefinition<StorageSyncJob>.Empty)
            .SortByDescending(job => job.CreatedAt)
            .Limit(50)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var names = await NamesAsync(cancellationToken).ConfigureAwait(false);

        var retentionDays = await RetentionDaysAsync(cancellationToken).ConfigureAwait(false);

        return [.. jobs.Select(job => Map(job, names, retentionDays))];
    }

    public async Task<StorageSyncJobResponse> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
        => Map(await LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false),
               await NamesAsync(cancellationToken).ConfigureAwait(false),
               await RetentionDaysAsync(cancellationToken).ConfigureAwait(false));

    public async Task<StorageSyncJobResponse> StartJobAsync(
        string mode, Guid targetId, bool switchPrimaryOnComplete, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<StorageSyncMode>(mode, ignoreCase: true, out var syncMode))
        {
            throw Invalid("mode", "storage.modeInvalid");
        }

        if (await ActiveJobAsync(cancellationToken).ConfigureAwait(false) is { } running)
        {
            throw Invalid("mode", "storage.syncRunning", ("status", running.Status));
        }

        var source = await _context.StorageProviders
            .Find(p => p.Role == StorageProviderRole.Primary)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw Invalid("targetId", "storage.noPrimary");

        var target = await LoadAsync(targetId, cancellationToken).ConfigureAwait(false);

        if (target.Id == source.Id)
        {
            throw Invalid("targetId", "storage.targetIsSource");
        }

        if (!target.IsVerified)
        {
            throw Invalid("targetId", "storage.targetUnverified");
        }

        var actor = Actor(UserMessage.Of("storage.reason.syncMode", ("mode", syncMode)).Encode());

        if (syncMode == StorageSyncMode.Mirror)
        {
            // Mirror mode is the target holding the secondary role; the job is
            // the initial copy that brings it level.
            var previous = await _context.StorageProviders
                .Find(p => p.Role == StorageProviderRole.Secondary && p.Id != target.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var holder in previous)
            {
                holder.Deactivate(actor, _clock.UtcNow);
                await SaveAsync(holder, cancellationToken).ConfigureAwait(false);
            }

            target.MakeSecondary(actor, _clock.UtcNow);
            await SaveAsync(target, cancellationToken).ConfigureAwait(false);
        }

        var job = new StorageSyncJob
        {
            Mode = syncMode,
            SourceId = source.Id,
            TargetId = target.Id,
            SwitchPrimaryOnComplete = syncMode == StorageSyncMode.Migration && switchPrimaryOnComplete,
            StartedBy = _currentUser.AdminUserId,
        };

        await AggregateWriter.InsertAsync(_context, _context.StorageSyncJobs, job, cancellationToken)
            .ConfigureAwait(false);

        // Dual-write starts now, not when a worker first picks the job up.
        await ChangedAsync(target.Id, cancellationToken).ConfigureAwait(false);

        _scheduler.Enqueue(job.Id);

        return Map(
            job,
            await NamesAsync(cancellationToken).ConfigureAwait(false),
            await RetentionDaysAsync(cancellationToken).ConfigureAwait(false));
    }

    public Task<StorageSyncJobResponse> PauseJobAsync(Guid jobId, CancellationToken cancellationToken)
        => TransitionAsync(jobId, job => job.Pause(Actor("storage.reason.paused"), _clock.UtcNow), enqueue: false, cancellationToken);

    public Task<StorageSyncJobResponse> ResumeJobAsync(Guid jobId, CancellationToken cancellationToken)
        => TransitionAsync(jobId, job => job.Start(Actor("storage.reason.resumed"), _clock.UtcNow), enqueue: true, cancellationToken);

    public Task<StorageSyncJobResponse> CancelJobAsync(Guid jobId, CancellationToken cancellationToken)
        => TransitionAsync(jobId, job => job.Cancel(Actor("storage.reason.cancelled"), _clock.UtcNow), enqueue: false, cancellationToken);

    public Task<StorageSyncJobResponse> RetryFailuresAsync(Guid jobId, CancellationToken cancellationToken)
        => TransitionAsync(jobId, job => job.RetryVerification(Actor("storage.reason.retry"), _clock.UtcNow), enqueue: true, cancellationToken);

    public async Task<IReadOnlyList<StorageSyncFailureResponse>> ListFailuresAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var failures = await _context.StorageSyncFailures
            .Find(f => f.JobId == jobId)
            .SortByDescending(f => f.LastAttemptAt)
            .Limit(500)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. failures.Select(f => new StorageSyncFailureResponse(
            f.Bucket, f.Key, f.Error, f.Attempts, f.LastAttemptAt, f.ResolvedAt))];
    }

    public async Task<StorageSyncJobResponse> RequestSourcePurgeAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        var retentionDays = await RetentionDaysAsync(cancellationToken).ConfigureAwait(false);

        var allowedFrom = job.SourcePurgeAllowedFrom(retentionDays)
            ?? throw Invalid("job", "storage.purgeAfterMigration");

        if (_clock.UtcNow < allowedFrom)
        {
            throw Invalid(
                "job",
                "storage.purgeTooEarly",
                ("days", retentionDays),
                ("from", allowedFrom.ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
        }

        var source = await LoadAsync(job.SourceId, cancellationToken).ConfigureAwait(false);

        if (source.Role != StorageProviderRole.Inactive)
        {
            // Primary is where new files go; a mirror's secondary is a live copy.
            throw Invalid(
                "job",
                "storage.purgeSourceInUse",
                ("name", source.Name),
                ("role", source.Role));
        }

        if (job.SourcePurgeRequestedAt is null)
        {
            job.RequestSourcePurge(retentionDays, Actor("storage.reason.purgeConfirmed"), _clock.UtcNow);

            await AggregateWriter.ReplaceAsync(_context, _context.StorageSyncJobs, job, cancellationToken)
                .ConfigureAwait(false);
        }

        // Also when already requested but not finished: a purge that stopped
        // part-way is picked up where the listing now starts.
        if (job.SourcePurgedAt is null)
        {
            var id = job.Id;
            _jobs.Enqueue<StorageSourcePurgeJob>(purge => purge.RunAsync(id, CancellationToken.None));
        }

        return Map(job, await NamesAsync(cancellationToken).ConfigureAwait(false), retentionDays);
    }

    public async Task DeleteProviderAsync(Guid providerId, CancellationToken cancellationToken)
    {
        var provider = await LoadAsync(providerId, cancellationToken).ConfigureAwait(false);

        if (provider.Role != StorageProviderRole.Inactive)
        {
            throw Invalid("role", "storage.deleteActive");
        }

        var inJob = await _context.StorageSyncJobs
            .Find(job => (job.SourceId == providerId || job.TargetId == providerId)
                         && (job.Status == StorageSyncStatus.Pending
                             || job.Status == StorageSyncStatus.Running
                             || job.Status == StorageSyncStatus.Paused
                             || job.Status == StorageSyncStatus.Verifying))
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);

        if (inJob)
        {
            throw Invalid("role", "storage.deleteInSync");
        }

        var filter = Builders<Domain.Files.StoredFile>.Filter;
        var referenced = await _context.Files
            .Find(filter.Or(
                filter.Eq("Original.ProviderId", providerId),
                filter.Eq("Pdf.ProviderId", providerId),
                filter.Eq("Versions.Object.ProviderId", providerId)))
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);

        if (referenced)
        {
            // Reads follow the recorded provider: deleting it would make those
            // files unreadable. A migration moves them off first.
            throw Invalid("role", "storage.deleteHasFiles");
        }

        await _context.StorageProviders.DeleteOneAsync(p => p.Id == providerId, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Storage provider {Name} was deleted.", provider.Name);

        await ChangedAsync(providerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartRewrapAsync(CancellationToken cancellationToken)
    {
        var providers = await _context.StorageProviders
            .Find(FilterDefinition<StorageProvider>.Empty)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // One background job per provider and bucket, so a failure costs one
        // part of the pass rather than all of it.
        foreach (var provider in providers)
        {
            foreach (var bucket in Enum.GetValues<StorageBucket>())
            {
                var providerId = provider.Id;
                _jobs.Enqueue<KeyRewrapJob>(job => job.RunAsync(providerId, bucket, null, CancellationToken.None));
            }
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<StorageSyncJobResponse> TransitionAsync(
        Guid jobId, Action<StorageSyncJob> transition, bool enqueue, CancellationToken cancellationToken)
    {
        var job = await LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false);

        try
        {
            transition(job);
        }
        catch (IllegalStateTransitionException)
        {
            throw Invalid("action", "storage.actionNotNow", ("status", job.Status));
        }

        if (!job.IsActive)
        {
            job.ReleaseLease();
        }

        // The worker writes progress to the same document; if it got there
        // first, the latest copy is reloaded and the transition applied again.
        try
        {
            await AggregateWriter.ReplaceAsync(_context, _context.StorageSyncJobs, job, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ConcurrencyException)
        {
            job = await LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            transition(job);

            if (!job.IsActive)
            {
                job.ReleaseLease();
            }

            await AggregateWriter.ReplaceAsync(_context, _context.StorageSyncJobs, job, cancellationToken)
                .ConfigureAwait(false);
        }

        await ChangedAsync(job.TargetId, cancellationToken).ConfigureAwait(false);

        if (enqueue)
        {
            _scheduler.Enqueue(job.Id);
        }

        return Map(
            job,
            await NamesAsync(cancellationToken).ConfigureAwait(false),
            await RetentionDaysAsync(cancellationToken).ConfigureAwait(false));
    }

    private Task<StorageSyncJob?> ActiveJobAsync(CancellationToken cancellationToken)
        => _context.StorageSyncJobs
            .Find(job => job.Status == StorageSyncStatus.Pending
                         || job.Status == StorageSyncStatus.Running
                         || job.Status == StorageSyncStatus.Paused
                         || job.Status == StorageSyncStatus.Verifying)
            .FirstOrDefaultAsync(cancellationToken)!;

    private async Task<StorageProvider> LoadAsync(Guid id, CancellationToken cancellationToken)
        => await _context.StorageProviders.Find(p => p.Id == id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("storageProvider");

    private async Task<StorageSyncJob> LoadJobAsync(Guid id, CancellationToken cancellationToken)
        => await _context.StorageSyncJobs.Find(j => j.Id == id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("syncJob");

    private Task SaveAsync(StorageProvider provider, CancellationToken cancellationToken)
        => AggregateWriter.ReplaceAsync(_context, _context.StorageProviders, provider, cancellationToken);

    private async Task ChangedAsync(Guid providerId, CancellationToken cancellationToken)
    {
        _registry.Invalidate();

        // The other processes drop their caches too (plan section 2.10: no
        // restart). Inside the request's transaction this goes through the
        // outbox, so it is sent only if the change commits.
        await _events.PublishAsync(new Contracts.StorageConfigChanged(providerId), cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<int> RetentionDaysAsync(CancellationToken cancellationToken)
        => _settings.GetIntAsync(Domain.Settings.SettingKeys.StorageSourceRetentionDays, 30, cancellationToken);

    private async Task<Dictionary<Guid, string>> NamesAsync(CancellationToken cancellationToken)
        => (await _context.StorageProviders.Find(FilterDefinition<StorageProvider>.Empty)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(p => p.Id, p => p.Name);

    private async Task EnsureUniqueNameAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var trimmed = name.Trim();

        var taken = await _context.StorageProviders
            .Find(p => p.Name == trimmed && p.Id != (exceptId ?? Guid.Empty))
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);

        if (taken)
        {
            throw Invalid("name", "storage.nameTaken");
        }
    }

    private static void Validate(StorageProviderInput input, bool requireSecret)
    {
        ArgumentNullException.ThrowIfNull(input);

        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            errors.Add(new("name", "storage.nameRequired"));
        }

        if (!Enum.TryParse<StorageProviderType>(input.Type, ignoreCase: true, out _))
        {
            errors.Add(new("type", "storage.typeInvalid"));
        }

        if (!Uri.TryCreate(input.Endpoint?.Trim(), UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            errors.Add(new("endpoint", "storage.endpointInvalid"));
        }

        // On an edit both keys may be left empty to keep the stored ones: the
        // panel shows neither, so it cannot send them back.
        if (requireSecret && string.IsNullOrWhiteSpace(input.AccessKey))
        {
            errors.Add(new("accessKey", "storage.accessKeyRequired"));
        }

        if (requireSecret && string.IsNullOrEmpty(input.SecretKey))
        {
            errors.Add(new("secretKey", "storage.secretKeyRequired"));
        }

        if (input.Buckets is null
            || string.IsNullOrWhiteSpace(input.Buckets.Files)
            || string.IsNullOrWhiteSpace(input.Buckets.Receipts)
            || string.IsNullOrWhiteSpace(input.Buckets.Temp))
        {
            errors.Add(new("buckets", "storage.bucketsRequired"));
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }

    private static StorageProviderType ParseType(string type)
        => Enum.Parse<StorageProviderType>(type, ignoreCase: true);

    private static string Region(StorageProviderInput input)
        => string.IsNullOrWhiteSpace(input.Region) ? "us-east-1" : input.Region.Trim();

    private static StorageBuckets Buckets(StorageProviderInput input) => new()
    {
        Files = input.Buckets.Files.Trim(),
        Receipts = input.Buckets.Receipts.Trim(),
        Temp = input.Buckets.Temp.Trim(),
    };

    private StorageProviderResponse Map(StorageProvider provider)
    {
        string hint;

        try
        {
            var access = _secrets.Unprotect(provider.AccessKeyCiphertext, provider.Id);
            hint = access.Length <= 4 ? "••••" : $"••••{access[^4..]}";
        }
        catch (InvalidOperationException)
        {
            // The storage secrets key changed underneath it. Shown, not thrown:
            // the panel is where somebody would go to fix it.
            hint = "(oxunmur)";
        }

        return new StorageProviderResponse(
            provider.Id,
            provider.Name,
            provider.Type.ToString(),
            provider.Endpoint,
            provider.Region,
            provider.UseSsl,
            hint,
            new StorageBucketsDto(provider.Buckets.Files, provider.Buckets.Receipts, provider.Buckets.Temp),
            provider.Role.ToString(),
            provider.HealthStatus.ToString(),
            Messages.WriteStored(provider.HealthMessage),
            provider.LastCheckedAt,
            provider.IsVerified);
    }

    private static StorageSyncJobResponse Map(
        StorageSyncJob job, IReadOnlyDictionary<Guid, string> names, int retentionDays) => new(
        job.Id,
        job.Mode.ToString(),
        job.SourceId,
        names.GetValueOrDefault(job.SourceId, "?"),
        job.TargetId,
        names.GetValueOrDefault(job.TargetId, "?"),
        job.Status.ToString(),
        job.TotalObjects,
        job.CopiedObjects,
        job.FailedObjects,
        job.TotalBytes,
        job.CopiedBytes,
        job.Checkpoint?.Bucket,
        job.SwitchPrimaryOnComplete,
        job.StartedAt,
        job.FinishedAt,
        Messages.WriteStored(job.FailureReason),
        job.SourcePurgeAllowedFrom(retentionDays),
        job.SourcePurgeRequestedAt,
        job.SourcePurgedAt,
        job.SourcePurgedObjects,
        job.SourceKeptObjects);

    private static StorageTestResponse Map(StorageTestResult result) => new(
        result.Succeeded,
        Messages.WriteStored(result.Message)!,
        [.. result.Buckets.Select(b => new StorageBucketTestDto(b.Bucket, b.Succeeded, Messages.WriteStored(b.Problem)))],
        (long)result.Elapsed.TotalMilliseconds);

    private static ValidationException Invalid(string field, string key, params (string Name, object? Value)[] arguments)
        => new([new ValidationError(field, key, arguments)]);
}
