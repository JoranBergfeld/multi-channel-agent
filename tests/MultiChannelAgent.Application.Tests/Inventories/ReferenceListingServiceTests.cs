using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class ReferenceListingServiceTests
{
    private readonly InventoryId _inventoryId = new(Guid.NewGuid());
    private readonly ParticipantId _participantId = new(Guid.NewGuid());
    private readonly InMemoryInventoryStore _inventories = new(_ => "Participant");
    private readonly InMemoryInventoryAuthorizationAuditStore _audits = new();
    private readonly InMemoryReferenceCatalogStore _catalog = new();

    private const string ConversationId = "web-conversation-1";

    private ReferenceListingService Service() =>
        new(_catalog, new InventoryAuthorizationService(_inventories, _audits));

    private Task<UnitListResult> ListUnitsAsync(int? pageSize = null, string? cursor = null) =>
        Service().ListUnitsAsync(
            _participantId, _inventoryId, pageSize, cursor, ConversationId, DateTimeOffset.UnixEpoch, CancellationToken.None);

    private Task<LocationListResult> ListLocationsAsync(int? pageSize = null, string? cursor = null) =>
        Service().ListLocationsAsync(
            _participantId, _inventoryId, pageSize, cursor, ConversationId, DateTimeOffset.UnixEpoch, CancellationToken.None);

    [Fact]
    public async Task A_non_member_is_told_nothing_that_would_reveal_the_Inventory_exists()
    {
        var result = await ListUnitsAsync();

        Assert.Equal(ReferenceListResultKind.NotFound, result.Kind);
        Assert.Contains(_audits.RecordedFacts, fact => fact.OutcomeCode == "Denied:NotAMember");
    }

    [Fact]
    public async Task A_Viewer_may_list_active_Units_with_their_aliases()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);
        _catalog.AddUnit(_inventoryId, "each", ["piece", "pieces", "pc", "pcs"], isReserved: true);
        _catalog.AddUnit(_inventoryId, "Cardboard Box", ["boxes"]);
        _catalog.AddUnit(_inventoryId, "Pallet", [], retired: true);

        var result = await ListUnitsAsync();

        Assert.Equal(ReferenceListResultKind.Completed, result.Kind);
        Assert.Equal(["Cardboard Box", "each"], result.View!.Units.Select(unit => unit.Name));
        Assert.Equal(["boxes"], result.View.Units[0].Aliases);
        Assert.False(result.View.HasMore);
        Assert.Null(result.View.NextCursor);
    }

    [Fact]
    public async Task A_Viewer_may_list_active_Locations()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);
        _catalog.AddLocation(_inventoryId, "Shelf B");
        _catalog.AddLocation(_inventoryId, "Shelf A");
        _catalog.AddLocation(_inventoryId, "Old Bay", retired: true);

        var result = await ListLocationsAsync();

        Assert.Equal(["Shelf A", "Shelf B"], result.View!.Locations.Select(location => location.Name));
    }

    [Fact]
    public async Task A_bounded_page_reports_that_more_remain_and_hands_back_a_resumable_cursor()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);
        _catalog.AddLocation(_inventoryId, "Shelf A");
        _catalog.AddLocation(_inventoryId, "Shelf B");
        _catalog.AddLocation(_inventoryId, "Shelf C");

        var first = await ListLocationsAsync(pageSize: 2);

        Assert.True(first.View!.HasMore);
        Assert.Equal(["Shelf A", "Shelf B"], first.View.Locations.Select(location => location.Name));

        var second = await ListLocationsAsync(pageSize: 2, first.View.NextCursor);

        Assert.False(second.View!.HasMore);
        Assert.Equal(["Shelf C"], second.View.Locations.Select(location => location.Name));
    }

    [Fact]
    public async Task A_page_size_outside_the_bound_is_answered_by_its_own_code()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);

        var result = await ListUnitsAsync(pageSize: ReferenceListQuery.MaxPageSize + 1);

        Assert.Equal(ReferenceListResultKind.Invalid, result.Kind);
        Assert.Equal("invalid_page_size", result.Code);
    }

    [Fact]
    public async Task A_cursor_issued_for_the_other_list_is_refused()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);
        var cursor = new ReferenceListCursor(
            ReferenceKind.Location, new ReferenceOrderKey("shelf a", Guid.NewGuid().ToString("D"))).Encode();

        var result = await ListUnitsAsync(cursor: cursor);

        Assert.Equal(ReferenceListResultKind.Invalid, result.Kind);
        Assert.Equal("invalid_cursor", result.Code);
    }

    /// <summary>
    /// A Unit's terms reach this record in whatever order a store happened to read them - and on SQL
    /// Server a nested collection has no guaranteed order at all. The record imposes one, so the same
    /// Unit reads identically whichever provider produced it and whichever order it was built in.
    /// </summary>
    [Fact]
    public async Task A_Units_aliases_read_the_same_however_they_reached_the_catalog()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);
        _catalog.AddUnit(_inventoryId, "Cardboard Box", ["boxes", "bx"]);
        _catalog.AddUnit(_inventoryId, "Crate", ["bx crate", "boxes crate"]);

        var result = await ListUnitsAsync();

        Assert.Equal(ReferenceListResultKind.Completed, result.Kind);
        Assert.Equal(["boxes", "bx"], result.View!.Units[0].Aliases);

        // The second Unit's aliases were added in the opposite order, and still read the same way.
        Assert.Equal(["boxes crate", "bx crate"], result.View.Units[1].Aliases);
    }

    /// <summary>The canonical name always leads, whatever it sorts as against the Unit's own aliases.</summary>
    [Fact]
    public void A_catalog_record_always_leads_with_the_canonical_term()
    {
        var record = new UnitCatalogRecord(
            new UnitId(Guid.NewGuid()),
            "Zebra",
            "zebra",
            [
                UnitTerm.Create("bx", isCanonical: false, isReserved: false),
                UnitTerm.Create("Zebra", isCanonical: true, isReserved: false),
                UnitTerm.Create("boxes", isCanonical: false, isReserved: false),
            ],
            IsReserved: false,
            Guid.NewGuid());

        Assert.True(record.Terms[0].IsCanonical);
        Assert.Equal(["Zebra", "boxes", "bx"], record.Terms.Select(term => term.Term));
        Assert.Equal(["boxes", "bx"], record.Aliases);
    }
}
