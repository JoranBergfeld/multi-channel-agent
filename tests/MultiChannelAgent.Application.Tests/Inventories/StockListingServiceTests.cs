using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class StockListingServiceTests
{
    private static readonly ParticipantId Viewer = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Stranger = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly InventoryId SomeInventory = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly UnitId EachUnit = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static StockEntrySummary Row(string name, decimal quantity, string idHex) => new(
        new StockEntryId(Guid.Parse($"{idHex}-0000-0000-0000-000000000000")),
        name,
        NameNormalization.Normalize(name),
        EachUnit,
        "each",
        null,
        null,
        null,
        Quantity.Create(quantity));

    private static (StockListingService Service, InMemoryStockStore StockStore) CreateService()
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        inventoryStore.GrantMembership(SomeInventory, Viewer, MembershipRole.Viewer, Now);
        var auditStore = new InMemoryInventoryAuthorizationAuditStore(new InMemoryActiveInventorySelectionStore());
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);
        var stockStore = new InMemoryStockStore();

        return (new StockListingService(stockStore, authorizationService), stockStore);
    }

    [Fact]
    public async Task Lists_on_hand_stock_by_default_excluding_zero_quantity_rows()
    {
        var (service, stockStore) = CreateService();
        stockStore.Add(SomeInventory, Row("Bolts", 5m, "10000000"));
        stockStore.Add(SomeInventory, Row("Nuts", 0m, "20000000"));

        var result = await service.ListAsync(
            Viewer, SomeInventory, includeZero: false, locationId: null, nameFilter: null, pageSize: null, cursor: null,
            channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(StockAccessOutcomeKind.Completed, result.Kind);
        var row = Assert.Single(result.View!.Rows);
        Assert.Equal("Bolts", row.Name);
    }

    [Fact]
    public async Task IncludeZero_true_surfaces_zero_quantity_rows_too()
    {
        var (service, stockStore) = CreateService();
        stockStore.Add(SomeInventory, Row("Bolts", 5m, "10000000"));
        stockStore.Add(SomeInventory, Row("Nuts", 0m, "20000000"));

        var result = await service.ListAsync(
            Viewer, SomeInventory, includeZero: true, locationId: null, nameFilter: null, pageSize: null, cursor: null,
            channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(2, result.View!.Rows.Count);
    }

    [Fact]
    public async Task Rows_are_returned_in_stable_deterministic_display_order()
    {
        var (service, stockStore) = CreateService();
        stockStore.Add(SomeInventory, Row("Zebra Bolts", 1m, "10000000"));
        stockStore.Add(SomeInventory, Row("Apple Bolts", 1m, "20000000"));

        var result = await service.ListAsync(
            Viewer, SomeInventory, includeZero: false, locationId: null, nameFilter: null, pageSize: null, cursor: null,
            channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(["Apple Bolts", "Zebra Bolts"], result.View!.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task A_page_larger_than_the_page_size_reports_has_more_and_a_next_cursor()
    {
        var (service, stockStore) = CreateService();
        for (var i = 0; i < 3; i++)
        {
            stockStore.Add(SomeInventory, Row($"Item {i}", 1m, $"{i + 1:00000000}"));
        }

        var result = await service.ListAsync(
            Viewer, SomeInventory, includeZero: false, locationId: null, nameFilter: null, pageSize: 2, cursor: null,
            channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(2, result.View!.Rows.Count);
        Assert.True(result.View.HasMore);
        Assert.NotNull(result.View.NextCursor);
    }

    [Fact]
    public async Task Resuming_from_a_cursor_continues_strictly_after_it()
    {
        var (service, stockStore) = CreateService();
        for (var i = 0; i < 3; i++)
        {
            stockStore.Add(SomeInventory, Row($"Item {i}", 1m, $"{i + 1:00000000}"));
        }

        var firstPage = await service.ListAsync(
            Viewer, SomeInventory, includeZero: false, locationId: null, nameFilter: null, pageSize: 2, cursor: null,
            channelConversationId: null, Now, CancellationToken.None);
        var secondPage = await service.ListAsync(
            Viewer, SomeInventory, includeZero: false, locationId: null, nameFilter: null, pageSize: 2, cursor: firstPage.View!.NextCursor,
            channelConversationId: null, Now, CancellationToken.None);

        Assert.Single(secondPage.View!.Rows);
        Assert.False(secondPage.View.HasMore);
        Assert.DoesNotContain(secondPage.View.Rows[0].Name, firstPage.View.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task A_non_member_gets_not_found_never_a_distinct_forbidden_signal()
    {
        var (service, stockStore) = CreateService();
        stockStore.Add(SomeInventory, Row("Bolts", 5m, "10000000"));

        var result = await service.ListAsync(
            Stranger, SomeInventory, includeZero: false, locationId: null, nameFilter: null, pageSize: null, cursor: null,
            channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(StockAccessOutcomeKind.NotFound, result.Kind);
        Assert.Null(result.View);
    }

    [Fact]
    public async Task A_malformed_cursor_is_reported_as_invalid_not_a_500()
    {
        var (service, _) = CreateService();

        var result = await service.ListAsync(
            Viewer, SomeInventory, includeZero: false, locationId: null, nameFilter: null, pageSize: null, cursor: "not-a-valid-cursor!!!",
            channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(StockAccessOutcomeKind.Invalid, result.Kind);
        Assert.Null(result.View);
    }

    [Fact]
    public async Task Quantity_is_exposed_as_exact_invariant_decimal_text()
    {
        var (service, stockStore) = CreateService();
        stockStore.Add(SomeInventory, Row("Bolts", 12.375m, "10000000"));

        var result = await service.ListAsync(
            Viewer, SomeInventory, includeZero: false, locationId: null, nameFilter: null, pageSize: null, cursor: null,
            channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal("12.375", result.View!.Rows[0].Quantity);
    }
}
