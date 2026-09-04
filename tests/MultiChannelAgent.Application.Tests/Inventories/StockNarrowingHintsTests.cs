using MultiChannelAgent.Application.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class StockNarrowingHintsTests
{
    [Fact]
    public void Units_are_offered_only_when_the_matches_actually_differ_by_Unit()
    {
        var oneUnit = StockNarrowingHints.FromFacets(new StockMatchFacets(["each"], ["Shelf A", "Shelf B"], false));
        var twoUnits = StockNarrowingHints.FromFacets(new StockMatchFacets(["each", "box"], ["Shelf A"], false));

        Assert.Empty(oneUnit.Units);
        Assert.Equal(["each", "box"], twoUnits.Units);
    }

    [Fact]
    public void Locations_are_offered_only_when_placement_actually_distinguishes_the_matches()
    {
        var onePlace = StockNarrowingHints.FromFacets(new StockMatchFacets(["each"], ["Shelf A"], false));
        var twoPlaces = StockNarrowingHints.FromFacets(new StockMatchFacets(["each"], ["Shelf A", "Shelf B"], false));

        Assert.Empty(onePlace.Locations);
        Assert.Equal(["Shelf A", "Shelf B"], twoPlaces.Locations);
    }

    [Fact]
    public void Unlocated_stock_is_offered_only_alongside_placed_stock()
    {
        var onlyUnlocated = StockNarrowingHints.FromFacets(new StockMatchFacets(["each"], [], true));
        var mixed = StockNarrowingHints.FromFacets(new StockMatchFacets(["each"], ["Shelf A"], true));

        Assert.False(onlyUnlocated.IncludesUnlocated);
        Assert.True(mixed.IncludesUnlocated);
        Assert.Equal(["Shelf A"], mixed.Locations);
    }

    [Fact]
    public void Nothing_that_would_change_the_answer_means_no_hints_at_all()
    {
        var hints = StockNarrowingHints.FromFacets(new StockMatchFacets(["each"], [], false));

        Assert.False(hints.HasAny);
    }
}
