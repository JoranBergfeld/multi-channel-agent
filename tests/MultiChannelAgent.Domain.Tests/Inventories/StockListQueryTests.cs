using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class StockListQueryTests
{
    private static readonly InventoryId SomeInventory = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public void Create_defaults_to_on_hand_only_with_the_default_page_size()
    {
        var query = StockListQuery.Create(SomeInventory, includeZero: false, unitId: null, locationId: null, unlocatedOnly: false, nameFilter: null, pageSize: null, cursor: null);

        Assert.False(query.IncludeZero);
        Assert.Equal(StockListQuery.DefaultPageSize, query.PageSize);
        Assert.Null(query.Cursor);
    }

    [Fact]
    public void Create_clamps_a_null_page_size_to_the_default()
    {
        var query = StockListQuery.Create(SomeInventory, includeZero: false, unitId: null, locationId: null, unlocatedOnly: false, nameFilter: null, pageSize: null, cursor: null);

        Assert.Equal(StockListQuery.DefaultPageSize, query.PageSize);
    }

    [Fact]
    public void Create_accepts_a_page_size_at_the_maximum_bound()
    {
        var query = StockListQuery.Create(SomeInventory, includeZero: false, unitId: null, locationId: null, unlocatedOnly: false, nameFilter: null, pageSize: StockListQuery.MaxPageSize, cursor: null);

        Assert.Equal(StockListQuery.MaxPageSize, query.PageSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_rejects_a_non_positive_page_size(int pageSize)
    {
        Assert.Throws<ArgumentException>(() =>
            StockListQuery.Create(SomeInventory, includeZero: false, unitId: null, locationId: null, unlocatedOnly: false, nameFilter: null, pageSize, cursor: null));
    }

    [Fact]
    public void Create_rejects_a_page_size_exceeding_the_maximum_bound()
    {
        Assert.Throws<ArgumentException>(() =>
            StockListQuery.Create(SomeInventory, includeZero: false, unitId: null, locationId: null, unlocatedOnly: false, nameFilter: null, StockListQuery.MaxPageSize + 1, cursor: null));
    }

    [Fact]
    public void Create_normalizes_a_blank_name_filter_to_null()
    {
        var query = StockListQuery.Create(SomeInventory, includeZero: false, unitId: null, locationId: null, unlocatedOnly: false, nameFilter: "   ", pageSize: null, cursor: null);

        Assert.Null(query.NameFilter);
    }

    [Fact]
    public void Create_rejects_a_malformed_cursor()
    {
        Assert.Throws<ArgumentException>(() =>
            StockListQuery.Create(SomeInventory, includeZero: false, unitId: null, locationId: null, unlocatedOnly: false, nameFilter: null, pageSize: null, cursor: "not-a-valid-cursor!!!"));
    }
}
