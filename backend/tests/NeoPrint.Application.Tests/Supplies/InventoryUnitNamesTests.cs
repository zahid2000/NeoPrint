using NeoPrint.Application.Supplies;
using NeoPrint.Domain.Supplies;

namespace NeoPrint.Application.Tests.Supplies;

public sealed class InventoryUnitNamesTests
{
    [Theory]
    [InlineData("bağlama", InventoryUnits.Ream)]
    [InlineData("Ədəd", InventoryUnits.Piece)]
    [InlineData(" пачка ", InventoryUnits.Ream)]
    [InlineData("шт", InventoryUnits.Piece)]
    [InlineData("box", InventoryUnits.Box)]
    [InlineData("roll", InventoryUnits.Roll)]
    [InlineData("kartric", "kartric")]
    public void A_known_unit_is_kept_as_its_code_whatever_language_it_was_written_in(string written, string kept)
    {
        // T-323: items written before the units had codes read like new ones.
        InventoryUnitNames.Normalize(written).Should().Be(kept);
        InventoryUnits.IsValid(kept).Should().BeTrue();
    }
}
