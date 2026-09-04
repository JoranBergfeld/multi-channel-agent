using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

/// <summary>
/// The one derivation of which Units and Locations a set of stock changes depends on. Two callers
/// need exactly this - a stored proposal, so retiring a reference can settle it, and the change-set
/// writer, so it can hold those references while it writes - and they must agree, or a reference one
/// of them protects is one the other never locks.
/// </summary>
public class StockReferenceDependenciesTests
{
    private static readonly UnitId Each = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly UnitId Box = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly LocationId ShelfA = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly LocationId ShelfB = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));

    private static ProposedEntryState State(UnitId unitId, LocationId? locationId) => new(
        new StockEntryId(Guid.NewGuid()),
        "Steel Bolts",
        "steel bolts",
        unitId,
        "each",
        locationId,
        locationId is null ? null : "Shelf",
        Note: null,
        Quantity.Zero,
        Quantity.Create(1m),
        Retired: false);

    private static ProposedChange Move(UnitId unitId, LocationId? from, LocationId? to) => new()
    {
        Order = 1,
        Kind = StockMutationKind.Move,
        Effect = StockChangeEffectKind.Placed,
        Source = State(unitId, from),
        Destination = State(unitId, to),
    };

    [Fact]
    public void Both_the_source_and_the_destination_of_every_change_are_dependencies()
    {
        var changes = new[] { Move(Each, ShelfA, ShelfB) };

        Assert.Equal([Each], StockReferenceDependencies.UnitsOf(changes, []));
        Assert.Equal([ShelfA, ShelfB], StockReferenceDependencies.LocationsOf(changes, []));
    }

    [Fact]
    public void An_expected_absence_names_a_reference_the_set_depends_on_too()
    {
        var absences = new[] { new ExpectedEquivalentStockAbsence("steel bolts", Box, ShelfB) };

        Assert.Equal([Box], StockReferenceDependencies.UnitsOf([], absences));
        Assert.Equal([ShelfB], StockReferenceDependencies.LocationsOf([], absences));
    }

    [Fact]
    public void Unlocated_contributes_no_Location_because_it_is_the_absence_of_one()
    {
        var changes = new[] { Move(Each, null, null) };

        Assert.Empty(StockReferenceDependencies.LocationsOf(changes, []));
        Assert.Equal([Each], StockReferenceDependencies.UnitsOf(changes, []));
    }

    [Fact]
    public void A_reference_named_more_than_once_is_reported_once_in_the_order_it_was_first_seen()
    {
        var changes = new[] { Move(Box, ShelfB, ShelfA), Move(Each, ShelfA, ShelfB) };
        var absences = new[] { new ExpectedEquivalentStockAbsence("steel bolts", Box, ShelfB) };

        Assert.Equal([Box, Each], StockReferenceDependencies.UnitsOf(changes, absences));
        Assert.Equal([ShelfB, ShelfA], StockReferenceDependencies.LocationsOf(changes, absences));
    }

    [Fact]
    public void A_set_that_touches_nothing_depends_on_nothing()
    {
        Assert.Empty(StockReferenceDependencies.UnitsOf([], []));
        Assert.Empty(StockReferenceDependencies.LocationsOf([], []));
    }
}
