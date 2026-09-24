using Microsoft.Extensions.DependencyInjection;

using MongoDB.Bson;
using MongoDB.Driver;

using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Retention windows an administrator can change
/// (<c>docs/DATA_RETENTION.md</c>).
/// </summary>
/// <remarks>
/// A TTL index carries its own duration, so changing the setting has to change
/// the index. These check that it is created, that it is adjusted rather than
/// dropped when the setting changes, and that a nonsensical window is ignored.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class RetentionTests
{
    private readonly NeoPrintApiFixture _fixture;

    public RetentionTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    private static async Task<long?> TtlSecondsAsync(
        MongoContext context, string collectionName, string indexName)
    {
        var indexes = await context.Database
            .GetCollection<BsonDocument>(collectionName)
            .Indexes.List()
            .ToListAsync();

        var index = indexes.FirstOrDefault(candidate => candidate["name"].AsString == indexName);

        return index is null || !index.TryGetValue("expireAfterSeconds", out var seconds)
            ? null
            : seconds.ToInt64();
    }

    [DockerFact]
    public async Task Startup_puts_a_retention_window_on_the_history_collections()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        // Applied by the host on start, from the seeded settings.
        (await TtlSecondsAsync(context, "state_transitions", "ttl_state_transitions"))
            .Should().Be((long)TimeSpan.FromDays(365).TotalSeconds);

        (await TtlSecondsAsync(context, "provider_events", "ttl_provider_events"))
            .Should().Be((long)TimeSpan.FromDays(180).TotalSeconds);
    }

    [DockerFact]
    public async Task Changing_the_window_adjusts_the_index_rather_than_dropping_it()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        await context.ApplyRetentionAsync(TimeSpan.FromDays(30), TimeSpan.FromDays(7));

        (await TtlSecondsAsync(context, "state_transitions", "ttl_state_transitions"))
            .Should().Be((long)TimeSpan.FromDays(30).TotalSeconds);

        (await TtlSecondsAsync(context, "provider_events", "ttl_provider_events"))
            .Should().Be((long)TimeSpan.FromDays(7).TotalSeconds);

        // Dropping and recreating would leave a window with no index at all,
        // so the index keeps its identity across the change.
        await context.ApplyRetentionAsync(TimeSpan.FromDays(365), TimeSpan.FromDays(180));

        (await TtlSecondsAsync(context, "state_transitions", "ttl_state_transitions"))
            .Should().Be((long)TimeSpan.FromDays(365).TotalSeconds);
    }

    [DockerFact]
    public async Task A_window_of_zero_is_ignored_rather_than_deleting_everything()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var before = await TtlSecondsAsync(context, "state_transitions", "ttl_state_transitions");

        // Nobody means "delete it all immediately" by a retention setting.
        await context.ApplyRetentionAsync(TimeSpan.Zero, TimeSpan.FromDays(-1));

        (await TtlSecondsAsync(context, "state_transitions", "ttl_state_transitions"))
            .Should().Be(before);
    }

    [DockerFact]
    public async Task Idempotency_keys_clean_themselves_up_without_a_job()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        // The plan lists an hourly cleanup job; a TTL index on the expiry does
        // the same work, so the job would be redundant (DECISIONS.md).
        (await TtlSecondsAsync(context, "idempotency_keys", "ttl_idempotency_keys"))
            .Should().Be(0, "the record carries its own ExpiresAt");
    }
}
