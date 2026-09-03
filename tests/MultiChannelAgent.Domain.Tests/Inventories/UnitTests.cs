using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class UnitTests
{
    private static readonly InventoryId SomeInventory = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    // Every Inventory must start with the reserved `each` Unit and exactly the fixed aliases
    // `piece`, `pieces`, `pc`, and `pcs` - callers cannot vary this, so the factory takes no name.
    [Fact]
    public void CreateReservedEach_produces_the_fixed_canonical_name_and_aliases()
    {
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var unit = Unit.CreateReservedEach(SomeInventory, createdAt);

        Assert.Equal("each", unit.CanonicalName);
        Assert.True(unit.IsReserved);
        Assert.Equal(SomeInventory, unit.InventoryId);
        Assert.Equal(createdAt, unit.CreatedAt);
        Assert.Equal(["piece", "pieces", "pc", "pcs"], unit.Aliases);
        Assert.NotEqual(default, unit.Id.Value);
    }

    [Fact]
    public void CreateReservedEach_called_twice_produces_distinct_unit_ids()
    {
        var first = Unit.CreateReservedEach(SomeInventory, DateTimeOffset.UtcNow);
        var second = Unit.CreateReservedEach(SomeInventory, DateTimeOffset.UtcNow);

        Assert.NotEqual(first.Id, second.Id);
    }
}
