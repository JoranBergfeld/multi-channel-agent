using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class ReferenceListCursorTests
{
    private static readonly ReferenceOrderKey Key = new("shelf a", "0f8fad5b-d9cb-469f-a165-70867728950e");

    [Fact]
    public void A_cursor_round_trips_its_order_key_and_its_kind()
    {
        var encoded = new ReferenceListCursor(ReferenceKind.Location, Key).Encode();

        Assert.True(ReferenceListCursor.TryDecode(encoded, out var decoded));
        Assert.Equal(ReferenceKind.Location, decoded!.Kind);
        Assert.Equal(Key, decoded.OrderKey);
    }

    [Fact]
    public void An_absent_cursor_decodes_to_starting_from_the_first_page()
    {
        Assert.True(ReferenceListCursor.TryDecode(null, out var decoded));
        Assert.Null(decoded);

        Assert.True(ReferenceListCursor.TryDecode("   ", out var blank));
        Assert.Null(blank);
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("!!!!")]
    public void A_malformed_cursor_is_refused(string cursor) =>
        Assert.False(ReferenceListCursor.TryDecode(cursor, out _));

    [Fact]
    public void A_cursor_issued_for_Units_can_never_resume_a_Location_list()
    {
        var encoded = new ReferenceListCursor(ReferenceKind.Unit, Key).Encode();

        Assert.True(ReferenceListCursor.TryDecode(encoded, out var decoded));
        Assert.False(decoded!.Matches(ReferenceKind.Location));
        Assert.True(decoded.Matches(ReferenceKind.Unit));
    }

    [Fact]
    public void A_query_defaults_to_a_bounded_page_and_no_cursor()
    {
        var query = ReferenceListQuery.Create(new InventoryId(Guid.NewGuid()), ReferenceKind.Unit, pageSize: null, cursor: null);

        Assert.Equal(ReferenceListQuery.DefaultPageSize, query.PageSize);
        Assert.Null(query.Cursor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ReferenceListQuery.MaxPageSize + 1)]
    public void A_page_size_outside_the_bound_is_refused(int pageSize)
    {
        var invalid = Assert.Throws<ArgumentException>(() => ReferenceListQuery.Create(
            new InventoryId(Guid.NewGuid()), ReferenceKind.Unit, pageSize, cursor: null));

        Assert.Equal("pageSize", invalid.ParamName);
    }

    [Fact]
    public void A_cursor_from_the_other_kind_is_refused_by_the_query()
    {
        var encoded = new ReferenceListCursor(ReferenceKind.Unit, Key).Encode();

        var invalid = Assert.Throws<ArgumentException>(() => ReferenceListQuery.Create(
            new InventoryId(Guid.NewGuid()), ReferenceKind.Location, pageSize: null, encoded));

        Assert.Equal("cursor", invalid.ParamName);
    }

    [Fact]
    public void An_order_key_orders_by_normalized_name_then_identity_ordinally()
    {
        var keys = new[]
        {
            new ReferenceOrderKey("shelf b", "00000000-0000-0000-0000-000000000001"),
            new ReferenceOrderKey("shelf a", "ffffffff-ffff-ffff-ffff-ffffffffffff"),
            new ReferenceOrderKey("shelf a", "00000000-0000-0000-0000-000000000002"),
        };

        var ordered = keys.OrderBy(key => key, ReferenceOrderKey.Comparer).ToList();

        Assert.Equal("shelf a", ordered[0].NormalizedName);
        Assert.Equal("00000000-0000-0000-0000-000000000002", ordered[0].IdOrderKey);
        Assert.Equal("shelf a", ordered[1].NormalizedName);
        Assert.Equal("shelf b", ordered[2].NormalizedName);
    }
}
