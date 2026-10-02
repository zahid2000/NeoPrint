using System.Text.RegularExpressions;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Infrastructure.Persistence;

/// <inheritdoc cref="IKioskSearch" />
/// <remarks>
/// <para>
/// Near a point: <c>$geoNear</c> on the <c>2dsphere</c> index
/// (<see cref="MongoContext.KioskPointPath"/>), which sorts by distance and
/// reports it; the text and status filters ride along as its query.
/// </para>
/// <para>
/// The text is a case-insensitive "contains" over the name, the code and the
/// address, escaped so it is never a pattern of the caller's making. A
/// <c>$text</c> index would match whole words only - "28 May" would not find
/// "Bakı, 28 May metro" by "28 Ma" - and cannot be combined with
/// <c>$geoNear</c>; kiosks number in the hundreds, not millions.
/// </para>
/// </remarks>
public sealed class KioskSearch : IKioskSearch
{
    private const string DistanceField = "_distanceMeters";

    private readonly MongoContext _context;

    public KioskSearch(MongoContext context) => _context = context;

    public async Task<IReadOnlyList<KioskHit>> SearchAsync(KioskSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var filter = Filter(criteria);

        if (criteria.Near is not { } near)
        {
            var kiosks = await _context.Kiosks
                .Find(filter)
                .SortBy(kiosk => kiosk.Code)
                .Limit(criteria.Limit)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return [.. kiosks.Select(kiosk => new KioskHit(kiosk, null))];
        }

        var geoNear = new BsonDocument
        {
            { "near", new BsonDocument { { "type", "Point" }, { "coordinates", new BsonArray { near.Lng, near.Lat } } } },
            { "key", MongoContext.KioskPointPath },
            { "distanceField", DistanceField },
            { "spherical", true },
            { "query", Render(filter) },
        };

        if (criteria.RadiusKm is { } radius)
        {
            geoNear["maxDistance"] = radius * 1000;
        }

        var documents = await _context.Kiosks
            .Aggregate()
            .AppendStage<BsonDocument>(new BsonDocument("$geoNear", geoNear))
            .Limit(criteria.Limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. documents.Select(document =>
            {
                var meters = document[DistanceField].ToDouble();
                document.Remove(DistanceField);
                return new KioskHit(BsonSerializer.Deserialize<Kiosk>(document), meters);
            }),
        ];
    }

    private static FilterDefinition<Kiosk> Filter(KioskSearchCriteria criteria)
    {
        var builder = Builders<Kiosk>.Filter;
        var filter = builder.In(kiosk => kiosk.Status, criteria.Statuses);

        if (criteria.Id is { } id)
        {
            return filter & builder.Eq(kiosk => kiosk.Id, id);
        }

        if (!string.IsNullOrWhiteSpace(criteria.Text))
        {
            var pattern = new BsonRegularExpression(Regex.Escape(criteria.Text.Trim()), "i");

            filter &= builder.Or(
                builder.Regex(kiosk => kiosk.Name, pattern),
                builder.Regex(kiosk => kiosk.Code, pattern),
                builder.Regex("Location.Address", pattern));
        }

        return filter;
    }

    private static BsonDocument Render(FilterDefinition<Kiosk> filter)
    {
        var serializer = BsonSerializer.SerializerRegistry.GetSerializer<Kiosk>();
        return filter.Render(new RenderArgs<Kiosk>(serializer, BsonSerializer.SerializerRegistry));
    }
}
