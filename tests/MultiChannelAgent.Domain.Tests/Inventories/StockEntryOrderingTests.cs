using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class StockEntryOrderingTests
{
    private static StockEntrySummary Row(string name, string unit, string? location, string id) => new(
        new StockEntryId(Guid.Parse(id)),
        name,
        NameNormalization.Normalize(name),
        new UnitId(Guid.NewGuid()),
        unit,
        location is null ? null : new LocationId(Guid.NewGuid()),
        location,
        null,
        Quantity.Create(1m));

    [Fact]
    public void Orders_primarily_by_normalized_name_ordinal()
    {
        var zebra = Row("Zebra Bolts", "each", null, "11111111-1111-1111-1111-111111111111");
        var apple = Row("Apple Bolts", "each", null, "22222222-2222-2222-2222-222222222222");

        var ordered = new[] { zebra, apple }.OrderBy(r => r, StockEntryOrdering.ByDisplayOrder).ToList();

        Assert.Equal(["Apple Bolts", "Zebra Bolts"], ordered.Select(r => r.Name));
    }

    [Fact]
    public void Name_comparison_ignores_case_via_normalization()
    {
        var upper = Row("BOLTS", "each", null, "11111111-1111-1111-1111-111111111111");
        var lower = Row("bolts", "each", null, "11111111-1111-1111-1111-111111111111");

        Assert.Equal(0, StockEntryOrdering.ByDisplayOrder.Compare(upper, lower));
    }

    [Fact]
    public void Breaks_a_name_tie_by_unit_canonical_name()
    {
        var boxes = Row("Bolts", "box", null, "11111111-1111-1111-1111-111111111111");
        var each = Row("Bolts", "each", null, "22222222-2222-2222-2222-222222222222");

        var ordered = new[] { each, boxes }.OrderBy(r => r, StockEntryOrdering.ByDisplayOrder).ToList();

        Assert.Equal(["box", "each"], ordered.Select(r => r.UnitCanonicalName));
    }

    [Fact]
    public void Unlocated_stock_sorts_before_located_stock_on_a_name_and_unit_tie()
    {
        var located = Row("Bolts", "each", "Warehouse", "11111111-1111-1111-1111-111111111111");
        var unlocated = Row("Bolts", "each", null, "22222222-2222-2222-2222-222222222222");

        var ordered = new[] { located, unlocated }.OrderBy(r => r, StockEntryOrdering.ByDisplayOrder).ToList();

        Assert.Null(ordered[0].LocationName);
        Assert.Equal("Warehouse", ordered[1].LocationName);
    }

    [Fact]
    public void Breaks_a_full_tie_by_stock_entry_id_for_stability()
    {
        var first = Row("Bolts", "each", null, "11111111-1111-1111-1111-111111111111");
        var second = Row("Bolts", "each", null, "22222222-2222-2222-2222-222222222222");

        var ordered = new[] { second, first }.OrderBy(r => r, StockEntryOrdering.ByDisplayOrder).ToList();

        Assert.Equal([first.Id, second.Id], ordered.Select(r => r.Id));
    }
}
