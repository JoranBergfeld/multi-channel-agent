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

    private static (StockListingService Service, InMemoryStockStore StockStore, InMemoryInventoryReferenceStore References) CreateService()
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        inventoryStore.GrantMembership(SomeInventory, Viewer, MembershipRole.Viewer, Now);
        var auditStore = new InMemoryInventoryAuthorizationAuditStore(new InMemoryActiveInventorySelectionStore());
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);
        var stockStore = new InMemoryStockStore();
        var referenceStore = new InMemoryInventoryReferenceStore();

        return (new StockListingService(stockStore, referenceStore, authorizationService), stockStore, referenceStore);
    }

    private static StockListRequest Request(
        bool includeZero = false,
        string? unitReference = null,
        string? locationReference = null,
        bool unlocatedOnly = false,
        string? nameFilter = null,
        int? pageSize = null,
        string? cursor = null) => new()
        {
            IncludeZero = includeZero,
            UnitReference = unitReference,
            LocationReference = locationReference,
            UnlocatedOnly = unlocatedOnly,
            NameFilter = nameFilter,
            PageSize = pageSize,
            Cursor = cursor,
        };

    [Fact]
    public async Task Lists_on_hand_stock_by_default_excluding_zero_quantity_rows()
    {
        var (service, stockStore, _) = CreateService();
        stockStore.Add(SomeInventory, Row("Bolts", 5m, "10000000"));
        stockStore.Add(SomeInventory, Row("Nuts", 0m, "20000000"));

        var result = await service.ListAsync(
            Viewer, SomeInventory, Request(), channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(StockAccessOutcomeKind.Completed, result.Kind);
        var row = Assert.Single(result.View!.Rows);
        Assert.Equal("Bolts", row.Name);
    }

    [Fact]
    public async Task IncludeZero_true_surfaces_zero_quantity_rows_too()
    {
        var (service, stockStore, _) = CreateService();
        stockStore.Add(SomeInventory, Row("Bolts", 5m, "10000000"));
        stockStore.Add(SomeInventory, Row("Nuts", 0m, "20000000"));

        var result = await service.ListAsync(
            Viewer, SomeInventory, Request(includeZero: true), channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(2, result.View!.Rows.Count);
    }

    [Fact]
    public async Task Rows_are_returned_in_stable_deterministic_display_order()
    {
        var (service, stockStore, _) = CreateService();
        stockStore.Add(SomeInventory, Row("Zebra Bolts", 1m, "10000000"));
        stockStore.Add(SomeInventory, Row("Apple Bolts", 1m, "20000000"));

        var result = await service.ListAsync(
            Viewer, SomeInventory, Request(), channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(["Apple Bolts", "Zebra Bolts"], result.View!.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task A_page_larger_than_the_page_size_reports_has_more_and_a_next_cursor()
    {
        var (service, stockStore, _) = CreateService();
        for (var i = 0; i < 3; i++)
        {
            stockStore.Add(SomeInventory, Row($"Item {i}", 1m, $"{i + 1:00000000}"));
        }

        var result = await service.ListAsync(
            Viewer, SomeInventory, Request(pageSize: 2), channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(2, result.View!.Rows.Count);
        Assert.True(result.View.HasMore);
        Assert.NotNull(result.View.NextCursor);
    }

    [Fact]
    public async Task Resuming_from_a_cursor_continues_strictly_after_it()
    {
        var (service, stockStore, _) = CreateService();
        for (var i = 0; i < 3; i++)
        {
            stockStore.Add(SomeInventory, Row($"Item {i}", 1m, $"{i + 1:00000000}"));
        }

        var firstPage = await service.ListAsync(
            Viewer, SomeInventory, Request(pageSize: 2), channelConversationId: null, Now, CancellationToken.None);
        var secondPage = await service.ListAsync(
            Viewer, SomeInventory, Request(pageSize: 2, cursor: firstPage.View!.NextCursor),
            channelConversationId: null, Now, CancellationToken.None);

        Assert.Single(secondPage.View!.Rows);
        Assert.False(secondPage.View.HasMore);
        Assert.DoesNotContain(secondPage.View.Rows[0].Name, firstPage.View.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task A_non_member_gets_not_found_never_a_distinct_forbidden_signal()
    {
        var (service, stockStore, _) = CreateService();
        stockStore.Add(SomeInventory, Row("Bolts", 5m, "10000000"));

        var result = await service.ListAsync(
            Stranger, SomeInventory, Request(), channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(StockAccessOutcomeKind.NotFound, result.Kind);
        Assert.Null(result.View);
    }

    [Fact]
    public async Task A_malformed_cursor_is_reported_as_invalid_not_a_500()
    {
        var (service, _, _) = CreateService();

        var result = await service.ListAsync(
            Viewer, SomeInventory, Request(cursor: "not-a-valid-cursor!!!"), channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(StockAccessOutcomeKind.Invalid, result.Kind);
        Assert.Null(result.View);
    }

    [Fact]
    public async Task Quantity_is_exposed_as_exact_invariant_decimal_text()
    {
        var (service, stockStore, _) = CreateService();
        stockStore.Add(SomeInventory, Row("Bolts", 12.375m, "10000000"));

        var result = await service.ListAsync(
            Viewer, SomeInventory, Request(), channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal("12.375", result.View!.Rows[0].Quantity);
    }
    private static StockEntrySummary PlacedRow(string name, decimal quantity, string idHex, UnitId unitId, string unitName, LocationId? locationId, string? locationName) => new(
        new StockEntryId(Guid.Parse($"{idHex}-0000-0000-0000-000000000000")),
        name,
        NameNormalization.Normalize(name),
        unitId,
        unitName,
        locationId,
        locationName,
        null,
        Quantity.Create(quantity));

    // A Location filter names an exact Inventory-owned Location - by opaque id or by its exact name.
    [Fact]
    public async Task A_location_filter_resolves_an_exact_location_name_and_narrows_to_it()
    {
        var (service, stockStore, references) = CreateService();
        var shelfA = new LocationId(Guid.NewGuid());
        var shelfB = new LocationId(Guid.NewGuid());
        references.AddLocation(SomeInventory, shelfA, "Shelf A");
        references.AddLocation(SomeInventory, shelfB, "Shelf B");
        stockStore.Add(SomeInventory, PlacedRow("Bolts", 1m, "10000000", EachUnit, "each", shelfA, "Shelf A"));
        stockStore.Add(SomeInventory, PlacedRow("Bolts", 2m, "20000000", EachUnit, "each", shelfB, "Shelf B"));

        var result = await service.ListAsync(
            Viewer, SomeInventory, Request(locationReference: "shelf a"), channelConversationId: null, Now, CancellationToken.None);

        var row = Assert.Single(result.View!.Rows);
        Assert.Equal("Shelf A", row.Location);
    }

    [Fact]
    public async Task A_location_filter_also_accepts_the_opaque_location_id()
    {
        var (service, stockStore, references) = CreateService();
        var shelfA = new LocationId(Guid.NewGuid());
        references.AddLocation(SomeInventory, shelfA, "Shelf A");
        stockStore.Add(SomeInventory, PlacedRow("Bolts", 1m, "10000000", EachUnit, "each", shelfA, "Shelf A"));
        stockStore.Add(SomeInventory, Row("Nuts", 1m, "20000000"));

        var result = await service.ListAsync(
            Viewer, SomeInventory, Request(locationReference: shelfA.Value.ToString()), channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal("Bolts", Assert.Single(result.View!.Rows).Name);
    }

    // A Unit reference resolves through the Unit's whole active term namespace, so a caller may name
    // it however that Inventory says it - canonical name or alias - and never by a near miss.
    [Fact]
    public async Task A_unit_filter_resolves_an_active_alias_exactly()
    {
        var (service, stockStore, references) = CreateService();
        var boxUnit = new UnitId(Guid.NewGuid());
        references.AddUnit(SomeInventory, EachUnit, "each", "piece", "pieces", "pc", "pcs");
        references.AddUnit(SomeInventory, boxUnit, "box", "boxes");
        stockStore.Add(SomeInventory, Row("Bolts", 1m, "10000000"));
        stockStore.Add(SomeInventory, PlacedRow("Bolts", 2m, "20000000", boxUnit, "box", null, null));

        var result = await service.ListAsync(
            Viewer, SomeInventory, Request(unitReference: "Boxes"), channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal("box", Assert.Single(result.View!.Rows).Unit);
    }

    // Unknown references are never created implicitly, and never silently ignored either - ignoring
    // one would answer a different, wider question than the Participant asked.
    [Theory]
    [InlineData("Shelf Z", null)]
    [InlineData(null, "crates")]
    public async Task An_unknown_unit_or_location_reference_is_reported_as_reference_not_found(string? location, string? unit)
    {
        var (service, stockStore, _) = CreateService();
        stockStore.Add(SomeInventory, Row("Bolts", 1m, "10000000"));

        var result = await service.ListAsync(
            Viewer, SomeInventory, Request(locationReference: location, unitReference: unit),
            channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(StockAccessOutcomeKind.ReferenceNotFound, result.Kind);
        Assert.Equal("reference_not_found", result.Code);
        Assert.Null(result.View);
    }

    // "Unlocated" is the absence of a Location, not a place, so it is asked for explicitly.
    [Fact]
    public async Task An_unlocated_only_request_returns_only_stock_kept_nowhere_in_particular()
    {
        var (service, stockStore, references) = CreateService();
        var shelfA = new LocationId(Guid.NewGuid());
        references.AddLocation(SomeInventory, shelfA, "Shelf A");
        stockStore.Add(SomeInventory, PlacedRow("Bolts", 1m, "10000000", EachUnit, "each", shelfA, "Shelf A"));
        stockStore.Add(SomeInventory, Row("Nuts", 1m, "20000000"));

        var result = await service.ListAsync(
            Viewer, SomeInventory, Request(unlocatedOnly: true), channelConversationId: null, Now, CancellationToken.None);

        var row = Assert.Single(result.View!.Rows);
        Assert.Equal("Nuts", row.Name);
        Assert.Null(row.Location);
    }

    [Fact]
    public async Task Asking_for_a_location_and_for_unlocated_stock_at_once_is_invalid()
    {
        var (service, _, references) = CreateService();
        var shelfA = new LocationId(Guid.NewGuid());
        references.AddLocation(SomeInventory, shelfA, "Shelf A");

        var result = await service.ListAsync(
            Viewer, SomeInventory, Request(locationReference: "Shelf A", unlocatedOnly: true),
            channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(StockAccessOutcomeKind.Invalid, result.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(StockListQuery.MaxPageSize + 1)]
    public async Task A_page_size_outside_its_bounds_is_invalid_rather_than_silently_clamped(int pageSize)
    {
        var (service, _, _) = CreateService();

        var result = await service.ListAsync(
            Viewer, SomeInventory, Request(pageSize: pageSize), channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(StockAccessOutcomeKind.Invalid, result.Kind);
    }

    // A cursor answers "continue exactly this question": resuming it against a different question
    // would return a page that answers neither, silently skipping or repeating rows.
    [Fact]
    public async Task A_cursor_from_a_differently_shaped_request_is_rejected_rather_than_reinterpreted()
    {
        var (service, stockStore, _) = CreateService();
        for (var i = 0; i < 4; i++)
        {
            stockStore.Add(SomeInventory, Row($"Item {i}", 1m, $"{i + 1:00000000}"));
        }

        var firstPage = await service.ListAsync(
            Viewer, SomeInventory, Request(pageSize: 2), channelConversationId: null, Now, CancellationToken.None);

        var differentFilter = await service.ListAsync(
            Viewer, SomeInventory, Request(pageSize: 2, nameFilter: "Item 1", cursor: firstPage.View!.NextCursor),
            channelConversationId: null, Now, CancellationToken.None);
        var differentPageSize = await service.ListAsync(
            Viewer, SomeInventory, Request(pageSize: 3, cursor: firstPage.View.NextCursor),
            channelConversationId: null, Now, CancellationToken.None);
        var differentOnHandSetting = await service.ListAsync(
            Viewer, SomeInventory, Request(pageSize: 2, includeZero: true, cursor: firstPage.View.NextCursor),
            channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(StockAccessOutcomeKind.Invalid, differentFilter.Kind);
        Assert.Equal(StockAccessOutcomeKind.Invalid, differentPageSize.Kind);
        Assert.Equal(StockAccessOutcomeKind.Invalid, differentOnHandSetting.Kind);
    }

    [Fact]
    public async Task A_cursor_from_another_inventory_is_rejected()
    {
        var (service, stockStore, _) = CreateService();
        for (var i = 0; i < 3; i++)
        {
            stockStore.Add(SomeInventory, Row($"Item {i}", 1m, $"{i + 1:00000000}"));
        }

        var firstPage = await service.ListAsync(
            Viewer, SomeInventory, Request(pageSize: 2), channelConversationId: null, Now, CancellationToken.None);

        // Same Participant, same shape in every respect except the Inventory it was issued for.
        var otherInventory = new InventoryId(Guid.Parse("99999999-9999-9999-9999-999999999999"));
        var result = await service.ListAsync(
            Viewer, otherInventory, Request(pageSize: 2, cursor: firstPage.View!.NextCursor),
            channelConversationId: null, Now, CancellationToken.None);

        // Not authorized for that Inventory at all, so the non-disclosing answer comes first; the
        // cursor check is proven for an authorized Inventory by the shape test above.
        Assert.Equal(StockAccessOutcomeKind.NotFound, result.Kind);
    }
    // Every role that may read must actually be able to: an Editor and an Owner have all of a
    // Viewer's read access, and proving it for the Viewer alone would let a stricter-than-intended
    // check slip through for the other two.
    [Theory]
    [InlineData(MembershipRole.Viewer)]
    [InlineData(MembershipRole.Editor)]
    [InlineData(MembershipRole.Owner)]
    public async Task Viewer_editor_and_owner_can_all_list_stock(MembershipRole role)
    {
        var reader = new ParticipantId(Guid.Parse("55555555-5555-5555-5555-555555555555"));
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        inventoryStore.GrantMembership(SomeInventory, reader, role, Now);
        var auditStore = new InMemoryInventoryAuthorizationAuditStore(new InMemoryActiveInventorySelectionStore());
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);
        var stockStore = new InMemoryStockStore();
        stockStore.Add(SomeInventory, Row("Bolts", 5m, "10000000"));
        var service = new StockListingService(stockStore, new InMemoryInventoryReferenceStore(), authorizationService);

        var result = await service.ListAsync(reader, SomeInventory, Request(), channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(StockAccessOutcomeKind.Completed, result.Kind);
        Assert.Equal("Bolts", Assert.Single(result.View!.Rows).Name);
    }
}
