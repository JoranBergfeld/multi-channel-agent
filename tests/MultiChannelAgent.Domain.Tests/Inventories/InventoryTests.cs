using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class InventoryTests
{
    private static readonly ParticipantId Creator = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public void Create_trims_the_name_and_normalizes_client_request_id()
    {
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var inventory = Inventory.Create(
            name: "  Main Warehouse  ",
            createdBy: Creator,
            clientRequestId: "  req-1  ",
            createdAt: createdAt);

        Assert.Equal("Main Warehouse", inventory.Name);
        Assert.Equal("req-1", inventory.ClientRequestId);
        Assert.Equal(Creator, inventory.CreatedByParticipantId);
        Assert.Equal(createdAt, inventory.CreatedAt);
        Assert.NotEqual(default, inventory.Id.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_name(string blank)
    {
        Assert.Throws<ArgumentException>(() => Inventory.Create(blank, Creator, "req-1", DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_client_request_id(string blank)
    {
        Assert.Throws<ArgumentException>(() => Inventory.Create("Warehouse", Creator, blank, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Create_accepts_a_name_at_the_maximum_length()
    {
        var name = new string('a', Inventory.MaxNameLength);

        var inventory = Inventory.Create(name, Creator, "req-1", DateTimeOffset.UtcNow);

        Assert.Equal(name, inventory.Name);
    }

    [Fact]
    public void Create_rejects_a_name_over_the_maximum_length()
    {
        var name = new string('a', Inventory.MaxNameLength + 1);

        var exception = Assert.Throws<ArgumentException>(() => Inventory.Create(name, Creator, "req-1", DateTimeOffset.UtcNow));
        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void Create_accepts_a_client_request_id_at_the_maximum_length()
    {
        var clientRequestId = new string('r', Inventory.MaxClientRequestIdLength);

        var inventory = Inventory.Create("Warehouse", Creator, clientRequestId, DateTimeOffset.UtcNow);

        Assert.Equal(clientRequestId, inventory.ClientRequestId);
    }

    [Fact]
    public void Create_rejects_a_client_request_id_over_the_maximum_length()
    {
        var clientRequestId = new string('r', Inventory.MaxClientRequestIdLength + 1);

        var exception = Assert.Throws<ArgumentException>(() => Inventory.Create("Warehouse", Creator, clientRequestId, DateTimeOffset.UtcNow));
        Assert.Equal("clientRequestId", exception.ParamName);
    }

    [Fact]
    public void Create_rejects_a_null_name_without_throwing_a_null_reference_exception()
    {
        Assert.Throws<ArgumentException>(() => Inventory.Create(null!, Creator, "req-1", DateTimeOffset.UtcNow));
    }

    // The stable short identifier lets duplicate Inventory names be disambiguated in a view without
    // exposing the full internal GUID; it must be a deterministic function of the Inventory's own Id.
    [Fact]
    public void ShortId_is_a_deterministic_short_form_of_the_inventory_id()
    {
        var id = new InventoryId(Guid.Parse("aabbccdd-1111-2222-3333-444455556666"));

        Assert.Equal("aabbccdd", id.ShortId);
    }

    [Fact]
    public void Two_inventories_created_with_the_same_name_get_distinct_ids_and_short_ids()
    {
        var first = Inventory.Create("Warehouse", Creator, "req-1", DateTimeOffset.UtcNow);
        var second = Inventory.Create("Warehouse", Creator, "req-2", DateTimeOffset.UtcNow);

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.Id.ShortId, second.Id.ShortId);
    }
}
