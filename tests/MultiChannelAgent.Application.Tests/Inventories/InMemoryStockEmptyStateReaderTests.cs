using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

/// <summary>
/// Pins the contract the double and its SQL twin must both satisfy: an Inventory with zero Stock
/// Entry rows reports no stock, exactly like one that has never held any.
/// </summary>
public sealed class InMemoryStockEmptyStateReaderTests
{
    [Fact]
    public async Task An_Inventory_with_no_rows_at_all_reports_no_stock()
    {
        var reader = new InMemoryStockEmptyStateReader();

        Assert.False(await reader.AnyStockAsync(new InventoryId(Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task Setting_stock_and_then_clearing_it_is_reported_accurately()
    {
        var reader = new InMemoryStockEmptyStateReader();
        var inventory = new InventoryId(Guid.NewGuid());

        reader.SetAnyStock(inventory, true);
        Assert.True(await reader.AnyStockAsync(inventory, CancellationToken.None));

        reader.SetAnyStock(inventory, false);
        Assert.False(await reader.AnyStockAsync(inventory, CancellationToken.None));
    }
}
