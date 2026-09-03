using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class StockFindQueryTests
{
    private static readonly InventoryId SomeInventory = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public void Create_by_name_normalizes_and_trims_the_reference_text()
    {
        var query = StockFindQuery.ByName(SomeInventory, "  Steel   Bolts  ", unitId: null, locationId: null);

        Assert.Equal("steel bolts", query.NormalizedNameReference);
        Assert.Null(query.StockEntryId);
    }

    [Fact]
    public void Create_by_id_carries_no_name_reference()
    {
        var id = new StockEntryId(Guid.NewGuid());

        var query = StockFindQuery.ById(SomeInventory, id);

        Assert.Equal(id, query.StockEntryId);
        Assert.Null(query.NormalizedNameReference);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ByName_rejects_a_blank_reference(string? reference)
    {
        Assert.Throws<ArgumentException>(() => StockFindQuery.ByName(SomeInventory, reference, unitId: null, locationId: null));
    }
}
