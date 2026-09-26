using MongoDB.Driver;

using NeoPrint.Domain.Common;
using NeoPrint.Domain.Pricing;
using NeoPrint.Pricing;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// A price, once taken, does not move when the profile does
/// (plan section 2.3: "sonrakı profil dəyişiklikləri mövcud job-lara təsir etmir").
/// </summary>
/// <remarks>
/// The owner asked for this to be proven, so it is checked three ways: in
/// memory straight after the profile is edited (a snapshot sharing the
/// profile's dictionary would fail here), after a round trip through MongoDB
/// with the profile edited and saved in between, and against a fresh quote
/// from the edited profile, which must differ - otherwise the test would pass
/// even if the edit had not taken.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class PriceSnapshotTests
{
    private readonly NeoPrintApiFixture _fixture;

    public PriceSnapshotTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    /// <summary>100 colour A3 sides: every rate in the profile plays a part.</summary>
    private static readonly PrintSpec Order = new(
        SelectedPages: 50, PagesPerSheet: 1, Copies: 2, Duplex: true, Color: true, Paper: "A3");

    private static PricingProfile Profile() => new()
    {
        Name = "Test profile",
        BwPricePerSide = Money.FromAzn(0.10m),
        ColorPricePerSide = Money.FromAzn(0.30m),
        MinOrder = Money.FromAzn(0.20m),
        PaperMultipliers = new(StringComparer.Ordinal) { ["A4"] = 1m, ["A3"] = 2m },
        VolumeTiers =
        [
            new VolumeTier { MinSides = 50, DiscountPercent = 10m },
            new VolumeTier { MinSides = 100, DiscountPercent = 15m },
        ],
    };

    /// <summary>Every rate raised: a changed profile cannot be missed.</summary>
    private static void EditEverything(PricingProfile profile)
    {
        profile.ColorPricePerSide = Money.FromAzn(0.50m);
        profile.BwPricePerSide = Money.FromAzn(0.20m);
        profile.MinOrder = Money.FromAzn(1.00m);
        profile.PaperMultipliers["A3"] = 3m;
        profile.VolumeTiers.Clear();
        profile.VolumeTiers.Add(new VolumeTier { MinSides = 500, DiscountPercent = 5m });
    }

    [Fact]
    public void Editing_the_profile_in_memory_does_not_reach_the_snapshot()
    {
        var profile = Profile();
        var snapshot = PriceSnapshot.Take(profile, Order, DateTime.UtcNow);

        // 100 sides x 0.30 x 2 = 60.00, less 15% = 51.00.
        snapshot.Price.FinalPrice.Should().Be(51.00m);

        EditEverything(profile);

        // Unchanged, including the copied rates - the dictionary and the tier
        // list are the snapshot's own, not the profile's.
        snapshot.Price.FinalPrice.Should().Be(51.00m);
        snapshot.Rates.ColorPerSide.Should().Be(0.30m);
        snapshot.Rates.PaperMultipliers["A3"].Should().Be(2m);
        snapshot.Rates.Tiers.Should().HaveCount(2);
        snapshot.Recompute().FinalPrice.Should().Be(51.00m);

        // And the edit really took: priced now, the same order costs more.
        PriceSnapshot.Take(profile, Order, DateTime.UtcNow).Price.FinalPrice
            .Should().Be(150.00m, "100 x 0.50 x 3 with no tier reached");
    }

    [DockerFact]
    public async Task A_stored_snapshot_survives_the_profile_being_edited_and_saved()
    {
        var database = new MongoClient(_fixture.MongoConnectionString)
            .GetDatabase($"neoprint-pricing-{Guid.NewGuid():N}");

        try
        {
            var profiles = database.GetCollection<PricingProfile>("pricing_profiles");
            var orders = database.GetCollection<OrderDocument>("orders");

            var profile = Profile();
            await profiles.InsertOneAsync(profile);

            // What step 3's print job will do: take the snapshot and store it
            // with the order.
            var order = new OrderDocument
            {
                Id = Guid.NewGuid(),
                Snapshot = PriceSnapshot.Take(profile, Order, DateTime.UtcNow),
            };

            await orders.InsertOneAsync(order);

            // The administrator raises every price and saves.
            EditEverything(profile);
            profile.RowVersion++;
            await profiles.ReplaceOneAsync(p => p.Id == profile.Id, profile);

            var reloaded = await orders.Find(o => o.Id == order.Id).FirstAsync();

            reloaded.Snapshot.Price.FinalPrice.Should().Be(51.00m);
            reloaded.Snapshot.Total.Should().Be(Money.FromAzn(51.00m));
            reloaded.Snapshot.Rates.ColorPerSide.Should().Be(0.30m);
            reloaded.Snapshot.Rates.PaperMultipliers["A3"].Should().Be(2m);
            reloaded.Snapshot.Rates.Tiers.Select(t => t.DiscountPercent).Should().Equal(10m, 15m);
            reloaded.Snapshot.ProfileVersion.Should().Be(0, "the revision that produced the price");

            // Recomputed from what was stored, not from the profile.
            reloaded.Snapshot.Recompute().Should().Be(reloaded.Snapshot.Price);

            // The profile as saved now prices the same order differently.
            var current = await profiles.Find(p => p.Id == profile.Id).FirstAsync();
            PriceSnapshot.Take(current, Order, DateTime.UtcNow).Price.FinalPrice.Should().Be(150.00m);
        }
        finally
        {
            await new MongoClient(_fixture.MongoConnectionString).DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    /// <summary>Stands in for the print job, which arrives in step 3.</summary>
    private sealed class OrderDocument
    {
        public Guid Id { get; set; }

        public PriceSnapshot Snapshot { get; set; } = null!;
    }
}
