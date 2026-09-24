using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

using NeoPrint.Domain.Common;

namespace NeoPrint.Infrastructure.Persistence;

/// <summary>
/// Stores <see cref="Money"/> as a document with its amount and currency.
/// </summary>
/// <remarks>
/// A bare number would lose the currency, which is the thing this type exists
/// to keep. The amount is Decimal128, so it is exact - a double would turn
/// 0.10 into something that is nearly 0.10 (plan section 2.1).
///
/// A document written before Money existed holds a plain number, so that is
/// read as well and taken to be manat. That is what every stored amount was.
/// </remarks>
public sealed class MoneySerializer : SerializerBase<Money>
{
    private static readonly DecimalSerializer AmountSerializer = new(BsonType.Decimal128);

    public override Money Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        ArgumentNullException.ThrowIfNull(context);

        var reader = context.Reader;

        switch (reader.GetCurrentBsonType())
        {
            case BsonType.Document:
                return ReadDocument(context);

            case BsonType.Null:
                reader.ReadNull();
                return Money.Zero;

            default:
                // Written before Money existed: a plain amount, in manat.
                return Money.FromAzn(AmountSerializer.Deserialize(context, args));
        }
    }

    private static Money ReadDocument(BsonDeserializationContext context)
    {
        var reader = context.Reader;

        var amount = 0m;
        var currency = Money.Azn;

        reader.ReadStartDocument();

        while (reader.ReadBsonType() != BsonType.EndOfDocument)
        {
            switch (reader.ReadName(Utf8NameDecoder.Instance))
            {
                case "amount":
                    amount = AmountSerializer.Deserialize(context);
                    break;

                case "currency":
                    currency = reader.ReadString();
                    break;

                default:
                    reader.SkipValue();
                    break;
            }
        }

        reader.ReadEndDocument();

        return currency == Money.Azn
            ? Money.FromAzn(amount)
            // Only AZN exists today; anything else is a document from a future
            // build, and guessing at it would be worse than saying so.
            : throw new DomainException($"Stored amount is in {currency}, which this build cannot read.");
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, Money value)
    {
        ArgumentNullException.ThrowIfNull(context);

        var writer = context.Writer;

        writer.WriteStartDocument();
        writer.WriteName("amount");
        AmountSerializer.Serialize(context, args, value.Amount);
        writer.WriteName("currency");
        writer.WriteString(value.Currency);
        writer.WriteEndDocument();
    }
}
