using Microsoft.Extensions.DependencyInjection;

using MongoDB.Bson;
using MongoDB.Driver;

using NeoPrint.Domain.Common;
using NeoPrint.Domain.Pricing;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Money keeps its currency and its exactness through the database
/// (plan sections 2.1, 2.3).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MoneyPersistenceTests
{
    private readonly NeoPrintApiFixture _fixture;

    public MoneyPersistenceTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task An_amount_survives_a_round_trip_exactly()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var profile = new PricingProfile
        {
            Name = $"Money round trip {Guid.NewGuid():N}",
            BwPricePerSide = Money.FromAzn(0.10m),
            ColorPricePerSide = Money.FromAzn(0.30m),
            MinOrder = Money.FromAzn(0.20m),
        };

        await context.PricingProfiles.InsertOneAsync(profile);

        var reloaded = await context.PricingProfiles
            .Find(candidate => candidate.Id == profile.Id)
            .FirstAsync();

        // 0.10 as a double is not 0.10; Decimal128 keeps it exact.
        reloaded.BwPricePerSide.Amount.Should().Be(0.10m);
        reloaded.BwPricePerSide.Currency.Should().Be("AZN");
        reloaded.ColorPricePerSide.Amount.Should().Be(0.30m);
        reloaded.MinOrder.Amount.Should().Be(0.20m);
    }

    [DockerFact]
    public async Task An_amount_is_stored_with_its_currency()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var profile = new PricingProfile
        {
            Name = $"Stored shape {Guid.NewGuid():N}",
            MinOrder = Money.FromAzn(0.25m),
        };

        await context.PricingProfiles.InsertOneAsync(profile);

        var raw = await context.Database
            .GetCollection<BsonDocument>("pricing_profiles")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", profile.Id))
            .FirstAsync();

        // A bare number would lose the currency, which is the thing the type
        // exists to keep.
        var stored = raw["MinOrder"].AsBsonDocument;

        stored["currency"].AsString.Should().Be("AZN");
        stored["amount"].BsonType.Should().Be(BsonType.Decimal128);
        stored["amount"].AsDecimal.Should().Be(0.25m);
    }

    [DockerFact]
    public async Task A_document_written_before_money_existed_still_reads()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var id = Guid.NewGuid();

        // What an older build wrote: a plain amount, with no currency.
        await context.Database
            .GetCollection<BsonDocument>("pricing_profiles")
            .InsertOneAsync(new BsonDocument
            {
                ["_id"] = new BsonBinaryData(id, GuidRepresentation.Standard),
                ["Name"] = $"Legacy {id:N}",
                ["IsDefault"] = false,
                ["BwPricePerSide"] = new BsonDecimal128(0.12m),
                ["ColorPricePerSide"] = new BsonDecimal128(0.36m),
                ["MinOrder"] = new BsonDecimal128(0.24m),
                ["CreatedAt"] = DateTime.UtcNow,
            });

        var reloaded = await context.PricingProfiles
            .Find(candidate => candidate.Id == id)
            .FirstAsync();

        // Taken to be manat, which is what every stored amount was.
        reloaded.BwPricePerSide.Amount.Should().Be(0.12m);
        reloaded.BwPricePerSide.Currency.Should().Be("AZN");
        reloaded.MinOrder.Amount.Should().Be(0.24m);
    }

    [DockerFact]
    public async Task A_missing_amount_reads_as_zero_rather_than_failing()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var id = Guid.NewGuid();

        await context.Database
            .GetCollection<BsonDocument>("pricing_profiles")
            .InsertOneAsync(new BsonDocument
            {
                ["_id"] = new BsonBinaryData(id, GuidRepresentation.Standard),
                ["Name"] = $"No minimum {id:N}",
                ["IsDefault"] = false,
                ["MinOrder"] = BsonNull.Value,
                ["CreatedAt"] = DateTime.UtcNow,
            });

        var reloaded = await context.PricingProfiles
            .Find(candidate => candidate.Id == id)
            .FirstAsync();

        reloaded.MinOrder.Should().Be(Money.Zero);
    }
}
