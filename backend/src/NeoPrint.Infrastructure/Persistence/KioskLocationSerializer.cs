using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Infrastructure.Persistence;

/// <summary>
/// Stores a <see cref="KioskLocation"/> with a GeoJSON point beside its
/// coordinates, for the <c>2dsphere</c> index the kiosk search uses (plan
/// step 5).
/// </summary>
/// <remarks>
/// The point is <c>{ type: "Point", coordinates: [lng, lat] }</c> - longitude
/// first, as GeoJSON has it. It is written from <c>Lat</c> / <c>Lng</c> on
/// every save, so it cannot drift from them; on reading it is skipped, since
/// it says nothing they do not. The domain keeps plain numbers and knows
/// nothing of GeoJSON. Documents written before the point existed are given
/// one when the indexes are created (<see cref="MongoContext.EnsureIndexesAsync"/>).
/// </remarks>
public sealed class KioskLocationSerializer : SerializerBase<KioskLocation>
{
    /// <summary>The field the <c>2dsphere</c> index is on, inside <c>Location</c>.</summary>
    public const string PointField = "Point";

    public override KioskLocation Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        ArgumentNullException.ThrowIfNull(context);

        var reader = context.Reader;

        if (reader.GetCurrentBsonType() == BsonType.Null)
        {
            reader.ReadNull();
            return new KioskLocation();
        }

        var location = new KioskLocation();

        reader.ReadStartDocument();

        while (reader.ReadBsonType() != BsonType.EndOfDocument)
        {
            switch (reader.ReadName(Utf8NameDecoder.Instance))
            {
                case nameof(KioskLocation.Lat):
                    location.Lat = ReadNumber(reader);
                    break;

                case nameof(KioskLocation.Lng):
                    location.Lng = ReadNumber(reader);
                    break;

                case nameof(KioskLocation.Address):
                    location.Address = reader.GetCurrentBsonType() == BsonType.Null ? ReadNullAsEmpty(reader) : reader.ReadString();
                    break;

                default:
                    reader.SkipValue();
                    break;
            }
        }

        reader.ReadEndDocument();

        return location;
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, KioskLocation value)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(value);

        var writer = context.Writer;

        writer.WriteStartDocument();
        writer.WriteDouble(nameof(KioskLocation.Lat), value.Lat);
        writer.WriteDouble(nameof(KioskLocation.Lng), value.Lng);
        writer.WriteString(nameof(KioskLocation.Address), value.Address ?? string.Empty);
        writer.WriteName(PointField);
        writer.WriteStartDocument();
        writer.WriteString("type", "Point");
        writer.WriteName("coordinates");
        writer.WriteStartArray();
        writer.WriteDouble(value.Lng);
        writer.WriteDouble(value.Lat);
        writer.WriteEndArray();
        writer.WriteEndDocument();
        writer.WriteEndDocument();
    }

    private static double ReadNumber(IBsonReader reader) => reader.GetCurrentBsonType() switch
    {
        BsonType.Int32 => reader.ReadInt32(),
        BsonType.Int64 => reader.ReadInt64(),
        BsonType.Decimal128 => (double)reader.ReadDecimal128(),
        _ => reader.ReadDouble(),
    };

    private static string ReadNullAsEmpty(IBsonReader reader)
    {
        reader.ReadNull();
        return string.Empty;
    }
}
