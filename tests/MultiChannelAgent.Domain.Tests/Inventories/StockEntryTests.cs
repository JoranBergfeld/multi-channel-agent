using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class StockEntryTests
{
    private static readonly InventoryId SomeInventory = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly UnitId SomeUnit = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly LocationId SomeLocation = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_trims_the_name_and_computes_its_normalized_form()
    {
        var entry = StockEntry.Create(SomeInventory, SomeUnit, SomeLocation, "  Steel   Bolts  ", null, Quantity.Create(10m), CreatedAt);

        Assert.Equal("Steel   Bolts", entry.Name);
        Assert.Equal("steel bolts", entry.NormalizedName);
        Assert.Equal(SomeInventory, entry.InventoryId);
        Assert.Equal(SomeUnit, entry.UnitId);
        Assert.Equal(SomeLocation, entry.LocationId);
        Assert.Equal(10m, entry.Quantity.Value);
        Assert.Null(entry.Note);
        Assert.NotEqual(default, entry.Id.Value);
    }

    [Fact]
    public void Create_accepts_no_location_and_no_note()
    {
        var entry = StockEntry.Create(SomeInventory, SomeUnit, null, "Bolts", null, Quantity.Create(0m), CreatedAt);

        Assert.Null(entry.LocationId);
        Assert.Null(entry.Note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_name(string? name)
    {
        Assert.Throws<ArgumentException>(() => StockEntry.Create(SomeInventory, SomeUnit, null, name, null, Quantity.Create(1m), CreatedAt));
    }

    [Fact]
    public void Create_rejects_a_name_exceeding_the_maximum_length()
    {
        var tooLong = new string('a', StockEntry.MaxNameLength + 1);

        Assert.Throws<ArgumentException>(() => StockEntry.Create(SomeInventory, SomeUnit, null, tooLong, null, Quantity.Create(1m), CreatedAt));
    }

    [Fact]
    public void Create_rejects_a_note_exceeding_the_maximum_length()
    {
        var tooLong = new string('a', StockEntry.MaxNoteLength + 1);

        Assert.Throws<ArgumentException>(() => StockEntry.Create(SomeInventory, SomeUnit, null, "Bolts", tooLong, Quantity.Create(1m), CreatedAt));
    }

    [Fact]
    public void IsEquivalentTo_is_true_for_the_same_normalized_name_unit_and_location_in_the_same_inventory()
    {
        var first = StockEntry.Create(SomeInventory, SomeUnit, SomeLocation, "Steel Bolts", "batch A", Quantity.Create(1m), CreatedAt);
        var second = StockEntry.Create(SomeInventory, SomeUnit, SomeLocation, "  steel   bolts ", "batch B", Quantity.Create(5m), CreatedAt);

        Assert.True(first.IsEquivalentTo(second));
    }

    [Theory]
    [InlineData("Steel Bolts", "Copper Bolts")]
    public void IsEquivalentTo_is_false_for_a_different_name(string nameA, string nameB)
    {
        var first = StockEntry.Create(SomeInventory, SomeUnit, SomeLocation, nameA, null, Quantity.Create(1m), CreatedAt);
        var second = StockEntry.Create(SomeInventory, SomeUnit, SomeLocation, nameB, null, Quantity.Create(1m), CreatedAt);

        Assert.False(first.IsEquivalentTo(second));
    }

    [Fact]
    public void IsEquivalentTo_is_false_for_a_different_unit()
    {
        var otherUnit = new UnitId(Guid.NewGuid());
        var first = StockEntry.Create(SomeInventory, SomeUnit, SomeLocation, "Steel Bolts", null, Quantity.Create(1m), CreatedAt);
        var second = StockEntry.Create(SomeInventory, otherUnit, SomeLocation, "Steel Bolts", null, Quantity.Create(1m), CreatedAt);

        Assert.False(first.IsEquivalentTo(second));
    }

    [Fact]
    public void IsEquivalentTo_is_false_for_a_different_location_including_unlocated_versus_located()
    {
        var first = StockEntry.Create(SomeInventory, SomeUnit, SomeLocation, "Steel Bolts", null, Quantity.Create(1m), CreatedAt);
        var second = StockEntry.Create(SomeInventory, SomeUnit, null, "Steel Bolts", null, Quantity.Create(1m), CreatedAt);

        Assert.False(first.IsEquivalentTo(second));
    }

    [Fact]
    public void IsEquivalentTo_is_false_across_different_inventories()
    {
        var otherInventory = new InventoryId(Guid.NewGuid());
        var first = StockEntry.Create(SomeInventory, SomeUnit, SomeLocation, "Steel Bolts", null, Quantity.Create(1m), CreatedAt);
        var second = StockEntry.Create(otherInventory, SomeUnit, SomeLocation, "Steel Bolts", null, Quantity.Create(1m), CreatedAt);

        Assert.False(first.IsEquivalentTo(second));
    }
}
