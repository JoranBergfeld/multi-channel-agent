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

    [Fact]
    public void Encoding_and_then_decoding_a_cursor_round_trips_the_same_ordering_tuple()
    {
        var cursor = StockListCursor.FromRow(Row);

        var encoded = cursor.Encode();
        var decoded = StockListCursor.TryDecode(encoded, out var result);

        Assert.True(decoded);
        Assert.Equal(cursor, result);
    }

    [Fact]
    public void Encoding_an_unlocated_row_and_decoding_it_round_trips_a_null_location_name()
    {
        var unlocated = Row with { LocationId = null, LocationName = null };
        var cursor = StockListCursor.FromRow(unlocated);

        StockListCursor.TryDecode(cursor.Encode(), out var result);

        Assert.Equal(cursor, result);
        Assert.Null(result!.LocationName);
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
}
