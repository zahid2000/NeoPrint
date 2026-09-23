using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Users;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Recording an acceptance twice must not produce two consent records
/// (<c>docs/DATA_RETENTION.md</c>, <c>docs/IDEMPOTENCY.md</c>).
/// </summary>
/// <remarks>
/// A consent record is evidence: it says which version of which document a user
/// accepted, when, from where. Two rows for the same acceptance with different
/// timestamps make that evidence ambiguous, and a retried request must not be
/// able to rewrite it.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ConsentIdempotencyTests
{
    private readonly NeoPrintApiFixture _fixture;

    public ConsentIdempotencyTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    private static Consent Consent(Guid userId, string version, DateTime acceptedAt, string ip) => new()
    {
        UserId = userId,
        DocType = LegalDocumentType.Privacy,
        Version = version,
        AcceptedAt = acceptedAt,
        Ip = ip,
        DeviceId = "test-device",
    };

    [DockerFact]
    public async Task Accepting_the_same_document_version_twice_records_it_once()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var legal = scope.ServiceProvider.GetRequiredService<ILegalDocumentRepository>();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var userId = Guid.NewGuid();
        var first = new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);

        await legal.AddConsentsAsync([Consent(userId, "1.0", first, "10.0.0.1")]);

        // The same acceptance again: a retried request, or a second tap on the
        // button before the first response arrived.
        await legal.AddConsentsAsync([Consent(userId, "1.0", first.AddMinutes(5), "10.0.0.9")]);

        var stored = await context.Consents
            .Find(consent => consent.UserId == userId)
            .ToListAsync();

        stored.Should().HaveCount(1);

        // The first acceptance is what stands: that is when the user actually
        // accepted, and the second attempt must not rewrite the evidence.
        stored[0].AcceptedAt.Should().Be(first);
        stored[0].Ip.Should().Be("10.0.0.1");
    }

    [DockerFact]
    public async Task A_new_document_version_is_a_new_acceptance()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var legal = scope.ServiceProvider.GetRequiredService<ILegalDocumentRepository>();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var userId = Guid.NewGuid();
        var at = new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);

        await legal.AddConsentsAsync([Consent(userId, "1.0", at, "10.0.0.1")]);
        await legal.AddConsentsAsync([Consent(userId, "2.0", at.AddDays(30), "10.0.0.1")]);

        var stored = await context.Consents
            .Find(consent => consent.UserId == userId)
            .ToListAsync();

        // Version 2 is a separate consent, not a duplicate: the user has to
        // accept each published version (plan section 2.13).
        stored.Should().HaveCount(2);
        stored.Select(consent => consent.Version).Should().BeEquivalentTo(["1.0", "2.0"]);
    }

    [DockerFact]
    public async Task Two_requests_recording_the_same_acceptance_at_once_leave_one_record()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var userId = Guid.NewGuid();
        var at = new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);

        // Separate scopes, as two concurrent requests would have.
        var writers = Enumerable.Range(0, 5).Select(_ => _fixture.Services.CreateScope()).ToList();

        try
        {
            var writes = writers.Select(writer => writer.ServiceProvider
                .GetRequiredService<ILegalDocumentRepository>()
                .AddConsentsAsync([Consent(userId, "1.0", at, "10.0.0.1")]));

            // The unique index is what makes this safe; the repository turns
            // the loser's duplicate key error into a no-op rather than a 500.
            await Task.WhenAll(writes);
        }
        finally
        {
            foreach (var writer in writers)
            {
                writer.Dispose();
            }
        }

        var stored = await context.Consents
            .Find(consent => consent.UserId == userId)
            .ToListAsync();

        stored.Should().HaveCount(1);
    }
}
