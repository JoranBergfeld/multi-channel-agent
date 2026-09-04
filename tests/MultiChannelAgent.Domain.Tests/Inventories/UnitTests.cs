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

    [Fact]
    public void A_created_Unit_normalizes_and_bounds_its_canonical_name()
    {
        var inventoryId = new InventoryId(Guid.NewGuid());
        var createdAt = DateTimeOffset.UnixEpoch;

        var unit = Unit.Create(inventoryId, "  Cardboard   Box  ", ["Boxes", " BX "], createdAt);

        Assert.Equal("Cardboard Box", unit.CanonicalName);
        Assert.False(unit.IsReserved);
        Assert.True(unit.IsActive);
        Assert.Null(unit.RetiredAt);
        Assert.Equal(["Boxes", "BX"], unit.Aliases);
        Assert.NotEqual(Guid.Empty, unit.Id.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_Unit_name_must_not_be_blank(string? name) =>
        Assert.Throws<ArgumentException>(() => Unit.Create(new InventoryId(Guid.NewGuid()), name, [], DateTimeOffset.UnixEpoch));

    [Fact]
    public void A_Unit_name_must_not_exceed_the_column_it_is_stored_in() =>
        Assert.Throws<ArgumentException>(() => Unit.Create(
            new InventoryId(Guid.NewGuid()), new string('b', Unit.MaxNameLength + 1), [], DateTimeOffset.UnixEpoch));

    [Fact]
    public void A_Unit_alias_must_not_exceed_the_column_it_is_stored_in() =>
        Assert.Throws<ArgumentException>(() => Unit.Create(
            new InventoryId(Guid.NewGuid()), "box", [new string('b', Unit.MaxNameLength + 1)], DateTimeOffset.UnixEpoch));

    [Fact]
    public void The_reserved_each_Unit_still_carries_exactly_its_four_fixed_aliases()
    {
        var unit = Unit.CreateReservedEach(new InventoryId(Guid.NewGuid()), DateTimeOffset.UnixEpoch);

        Assert.Equal(Unit.ReservedEachCanonicalName, unit.CanonicalName);
        Assert.True(unit.IsReserved);
        Assert.True(unit.IsActive);
        Assert.Equal(["piece", "pieces", "pc", "pcs"], unit.Aliases);
    }

    [Theory]
    [InlineData("each")]
    [InlineData("EACH")]
    [InlineData("piece")]
    [InlineData("pieces")]
    [InlineData("pc")]
    [InlineData(" Pcs ")]
    public void Every_reserved_term_is_recognized_however_it_is_written(string term) =>
        Assert.True(Unit.IsReservedEachTerm(term));

    [Theory]
    [InlineData("box")]
    [InlineData("eaches")]
    [InlineData("piecework")]
    public void Nothing_else_is_a_reserved_term(string term) =>
        Assert.False(Unit.IsReservedEachTerm(term));

    [Fact]
    public void A_retired_Unit_keeps_its_identity_and_stops_being_active()
    {
        var unit = Unit.Create(new InventoryId(Guid.NewGuid()), "box", [], DateTimeOffset.UnixEpoch);
        var retiredAt = DateTimeOffset.UnixEpoch.AddDays(1);

        var retired = unit with { RetiredAt = retiredAt };

        Assert.Equal(unit.Id, retired.Id);
        Assert.Equal(unit.CanonicalName, retired.CanonicalName);
        Assert.False(retired.IsActive);
        Assert.Equal(retiredAt, retired.RetiredAt);
    }

    [Fact]
    public void A_Unit_term_carries_its_normalized_form_and_whether_it_is_fixed()
    {
        var canonical = UnitTerm.Create("Cardboard Box", isCanonical: true, isReserved: false);
        var alias = UnitTerm.Create(" boxes ", isCanonical: false, isReserved: false);

        Assert.Equal("Cardboard Box", canonical.Term);
        Assert.Equal("cardboard box", canonical.NormalizedTerm);
        Assert.True(canonical.IsCanonical);
        Assert.Equal("boxes", alias.Term);
        Assert.Equal("boxes", alias.NormalizedTerm);
        Assert.False(alias.IsCanonical);
        Assert.False(alias.IsReserved);
    }
}
