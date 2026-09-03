using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class StockListCursorTests
{
    private static readonly StockEntrySummary Row = new(
        new StockEntryId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        "Steel Bolts",
        "steel bolts",
        new UnitId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
        "each",
        new LocationId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
        "Warehouse",
        null,
        Quantity.Create(4m));

    private static readonly StockListQueryShape SomeShape = StockListQueryShape.Compute(
        new InventoryId(Guid.Parse("44444444-4444-4444-4444-444444444444")),
        includeZero: false,
        unitId: null,
        locationId: null,
        unlocatedOnly: false,
        normalizedNameFilter: null,
        pageSize: 20);

    [Fact]
    public void Encoding_and_then_decoding_a_cursor_round_trips_the_same_ordering_tuple()
    {
        var cursor = StockListCursor.FromRow(Row, SomeShape);

        var encoded = cursor.Encode();
        var decoded = StockListCursor.TryDecode(encoded, out var result);

        Assert.True(decoded);
        Assert.Equal(cursor, result);
    }

    [Fact]
    public void Encoding_an_unlocated_row_and_decoding_it_round_trips_a_null_location_name()
    {
        var unlocated = Row with { LocationId = null, LocationName = null };
        var cursor = StockListCursor.FromRow(unlocated, SomeShape);

        StockListCursor.TryDecode(cursor.Encode(), out var result);

        Assert.Equal(cursor, result);
        Assert.Equal(StockEntryOrderKey.UnlocatedOrderKey, result!.OrderKey.LocationOrderKey);
    }

    [Fact]
    public void A_null_or_blank_cursor_decodes_as_absent_rather_than_invalid()
    {
        Assert.True(StockListCursor.TryDecode(null, out var fromNull));
        Assert.Null(fromNull);

        Assert.True(StockListCursor.TryDecode("   ", out var fromBlank));
        Assert.Null(fromBlank);
    }

    [Theory]
    [InlineData("not-base64!!!")]
    [InlineData("dGhpcyBpcyBub3QgdmFsaWQgY3Vyc29yIGpzb24=")]
    public void A_malformed_cursor_fails_to_decode(string malformed)
    {
        var decoded = StockListCursor.TryDecode(malformed, out var result);

        Assert.False(decoded);
        Assert.Null(result);
    }
    // A cursor is only ever valid for the question that issued it, so it carries that question's
    // shape and version and can be checked against the request trying to resume it.
    [Fact]
    public void A_cursor_only_matches_the_shape_it_was_issued_for()
    {
        var cursor = StockListCursor.FromRow(Row, SomeShape);
        var differentShape = StockListQueryShape.Compute(
            new InventoryId(Guid.Parse("44444444-4444-4444-4444-444444444444")),
            includeZero: true,
            unitId: null,
            locationId: null,
            unlocatedOnly: false,
            normalizedNameFilter: null,
            pageSize: 20);

        Assert.True(cursor.Matches(SomeShape));
        Assert.False(cursor.Matches(differentShape));
    }

    [Fact]
    public void A_cursor_from_an_older_query_shape_version_never_matches_the_current_one()
    {
        var older = new StockListCursor(StockEntryOrderKey.From(Row), SomeShape with { Version = SomeShape.Version - 1 });

        StockListCursor.TryDecode(older.Encode(), out var decoded);

        Assert.False(decoded!.Matches(SomeShape));
    }
}
