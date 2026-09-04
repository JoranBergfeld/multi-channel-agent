using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class LocationTests
{
    private static readonly InventoryId SomeInventory = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_collapses_the_name_and_computes_its_normalized_form()
    {
        var location = Location.Create(SomeInventory, "  Main   Warehouse  ", CreatedAt);

        Assert.Equal("Main Warehouse", location.Name);
        Assert.Equal("main warehouse", location.NormalizedName);
        Assert.Equal(SomeInventory, location.InventoryId);
        Assert.Equal(CreatedAt, location.CreatedAt);
        Assert.NotEqual(default, location.Id.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_name(string? name)
    {
        Assert.Throws<ArgumentException>(() => Location.Create(SomeInventory, name, CreatedAt));
    }

    [Fact]
    public void Create_rejects_a_name_exceeding_the_maximum_length()
    {
        var tooLong = new string('a', Location.MaxNameLength + 1);

        Assert.Throws<ArgumentException>(() => Location.Create(SomeInventory, tooLong, CreatedAt));
    }

    [Fact]
    public void Create_called_twice_produces_distinct_location_ids()
    {
        var first = Location.Create(SomeInventory, "Warehouse", CreatedAt);
        var second = Location.Create(SomeInventory, "Warehouse", CreatedAt);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void A_created_Location_is_active_and_has_never_been_retired()
    {
        var location = Location.Create(new InventoryId(Guid.NewGuid()), "Shelf A", DateTimeOffset.UnixEpoch);

        Assert.True(location.IsActive);
        Assert.Null(location.RetiredAt);
    }

    [Fact]
    public void A_retired_Location_keeps_its_identity_and_stops_being_active()
    {
        var location = Location.Create(new InventoryId(Guid.NewGuid()), "Shelf A", DateTimeOffset.UnixEpoch);
        var retiredAt = DateTimeOffset.UnixEpoch.AddDays(1);

        var retired = location with { RetiredAt = retiredAt };

        Assert.Equal(location.Id, retired.Id);
        Assert.Equal(location.Name, retired.Name);
        Assert.Equal(location.NormalizedName, retired.NormalizedName);
        Assert.False(retired.IsActive);
        Assert.Equal(retiredAt, retired.RetiredAt);
    }
}
