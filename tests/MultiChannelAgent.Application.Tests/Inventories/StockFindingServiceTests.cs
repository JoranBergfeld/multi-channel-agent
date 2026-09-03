using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class StockFindingServiceTests
{
    private static readonly ParticipantId Viewer = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Stranger = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly InventoryId SomeInventory = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly UnitId EachUnit = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static StockEntrySummary Row(string name, string idHex) => new(
        new StockEntryId(Guid.Parse($"{idHex}-0000-0000-0000-000000000000")),
        name,
        NameNormalization.Normalize(name),
        EachUnit,
        "each",
        null,
        null,
        null,
        Quantity.Create(1m));

    private static (StockFindingService Service, InMemoryStockStore StockStore, InMemoryInventoryReferenceStore References) CreateService()
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        inventoryStore.GrantMembership(SomeInventory, Viewer, MembershipRole.Viewer, Now);
        var auditStore = new InMemoryInventoryAuthorizationAuditStore(new InMemoryActiveInventorySelectionStore());
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);
        var stockStore = new InMemoryStockStore();
        var referenceStore = new InMemoryInventoryReferenceStore();

        return (new StockFindingService(stockStore, referenceStore, authorizationService), stockStore, referenceStore);
    }

    private static StockFindRequest Request(
        string? reference, string? unitReference = null, string? locationReference = null, bool unlocatedOnly = false) => new()
        {
            Reference = reference,
            UnitReference = unitReference,
            LocationReference = locationReference,
            UnlocatedOnly = unlocatedOnly,
        };

    [Fact]
    public async Task No_match_is_not_found()
    {
        var (service, _, _) = CreateService();

        var result = await service.FindAsync(Viewer, SomeInventory, Request("Bolts"), null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.NotFound, result.Kind);
        Assert.Null(result.View);
    }

    [Fact]
    public async Task Exactly_one_match_by_name_is_completed()
    {
        var (service, stockStore, _) = CreateService();
        stockStore.Add(SomeInventory, Row("Bolts", "10000000"));

        var result = await service.FindAsync(Viewer, SomeInventory, Request("  bolts  "), null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.Completed, result.Kind);
        var candidate = Assert.Single(result.View!.Candidates);
        Assert.Equal("Bolts", candidate.Name);
        Assert.False(result.View.HasMoreCandidates);
    }

    [Fact]
    public async Task An_opaque_id_reference_matches_by_id_before_any_name_matching()
    {
        var (service, stockStore, _) = CreateService();
        var target = Row("Bolts", "10000000");
        stockStore.Add(SomeInventory, target);
        stockStore.Add(SomeInventory, Row("Nuts", "20000000"));

        var result = await service.FindAsync(Viewer, SomeInventory, Request(target.Id.ToString()), null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.Completed, result.Kind);
        Assert.Equal(target.Id.ToString(), Assert.Single(result.View!.Candidates).Id);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task Two_to_five_matches_are_ambiguous_with_every_candidate(int count)
    {
        var (service, stockStore, _) = CreateService();
        for (var i = 0; i < count; i++)
        {
            stockStore.Add(SomeInventory, Row("Bolts", $"{i + 1:00000000}"));
        }

        var result = await service.FindAsync(Viewer, SomeInventory, Request("Bolts"), null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.Ambiguous, result.Kind);
        Assert.Equal(count, result.View!.Candidates.Count);
        Assert.False(result.View.HasMoreCandidates);
    }

    [Fact]
    public async Task More_than_five_matches_are_ambiguous_capped_at_five_with_more_flagged()
    {
        var (service, stockStore, _) = CreateService();
        for (var i = 0; i < 8; i++)
        {
            stockStore.Add(SomeInventory, Row("Bolts", $"{i + 1:00000000}"));
        }

        var result = await service.FindAsync(Viewer, SomeInventory, Request("Bolts"), null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.Ambiguous, result.Kind);
        Assert.Equal(5, result.View!.Candidates.Count);
        Assert.True(result.View.HasMoreCandidates);
    }

    [Fact]
    public async Task A_non_member_gets_not_found_never_a_distinct_forbidden_signal()
    {
        var (service, stockStore, _) = CreateService();
        stockStore.Add(SomeInventory, Row("Bolts", "10000000"));

        var result = await service.FindAsync(Stranger, SomeInventory, Request("Bolts"), null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.NotFound, result.Kind);
        Assert.Null(result.View);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_reference_is_invalid_not_a_500(string? reference)
    {
        var (service, _, _) = CreateService();

        var result = await service.FindAsync(Viewer, SomeInventory, Request(reference), null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.Invalid, result.Kind);
    }
    private static StockEntrySummary PlacedRow(string name, string idHex, UnitId unitId, string unitName, LocationId? locationId, string? locationName) => new(
        new StockEntryId(Guid.Parse($"{idHex}-0000-0000-0000-000000000000")),
        name,
        NameNormalization.Normalize(name),
        unitId,
        unitName,
        locationId,
        locationName,
        null,
        Quantity.Create(1m));

    // Beyond the candidate cap, a Participant needs something they can actually act on: the Units and
    // Locations the whole match set occupies, not just the ones the shown candidates happen to have.
    [Fact]
    public async Task An_oversized_match_set_offers_narrowing_drawn_from_all_the_matches()
    {
        var (service, stockStore, _) = CreateService();
        var boxUnit = new UnitId(Guid.NewGuid());
        var shelves = new[] { "Shelf A", "Shelf B", "Shelf C", "Shelf D", "Shelf E" };

        // Five matches in boxes (which sort first) fill the whole candidate cap, so the sixth - the
        // only one measured in `each` - is never shown.
        for (var i = 0; i < shelves.Length; i++)
        {
            stockStore.Add(SomeInventory, PlacedRow("Bolts", $"{i + 1:00000000}", boxUnit, "box", new LocationId(Guid.NewGuid()), shelves[i]));
        }

        stockStore.Add(SomeInventory, PlacedRow("Bolts", "60000000", EachUnit, "each", new LocationId(Guid.NewGuid()), "Shelf Z"));

        var result = await service.FindAsync(Viewer, SomeInventory, Request("Bolts"), null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.Ambiguous, result.Kind);
        Assert.Equal(5, result.View!.Candidates.Count);
        Assert.True(result.View.HasMoreCandidates);
        Assert.DoesNotContain(result.View.Candidates, candidate => candidate.Unit == "each");

        // Actionable precisely because it comes from the whole match set: narrowing to `each` is what
        // reaches the match no candidate on show could have led them to.
        Assert.Equal(["box", "each"], result.View.NarrowingHints.Units);
    }

    // Narrowing must be actionable: suggesting a Unit every match already shares would change nothing.
    [Fact]
    public async Task Narrowing_is_only_offered_where_the_matches_actually_differ()
    {
        var (service, stockStore, _) = CreateService();
        for (var i = 0; i < 3; i++)
        {
            stockStore.Add(SomeInventory, Row("Bolts", $"{i + 1:00000000}"));
        }

        var result = await service.FindAsync(Viewer, SomeInventory, Request("Bolts"), null, Now, CancellationToken.None);

        Assert.Empty(result.View!.NarrowingHints.Units);
        Assert.Empty(result.View.NarrowingHints.Locations);
        Assert.False(result.View.NarrowingHints.HasAny);
    }

    [Fact]
    public async Task Unlocated_stock_is_offered_as_narrowing_only_when_placement_distinguishes_the_matches()
    {
        var (service, stockStore, _) = CreateService();
        stockStore.Add(SomeInventory, PlacedRow("Bolts", "10000000", EachUnit, "each", new LocationId(Guid.NewGuid()), "Shelf A"));
        stockStore.Add(SomeInventory, Row("Bolts", "20000000"));

        var result = await service.FindAsync(Viewer, SomeInventory, Request("Bolts"), null, Now, CancellationToken.None);

        Assert.True(result.View!.NarrowingHints.IncludesUnlocated);
        Assert.Equal(["Shelf A"], result.View.NarrowingHints.Locations);
    }

    // The structured descriptor: an exact Unit or Location narrows the same reference deterministically.
    [Fact]
    public async Task An_exact_unit_narrowing_resolves_an_otherwise_ambiguous_reference()
    {
        var (service, stockStore, references) = CreateService();
        var boxUnit = new UnitId(Guid.NewGuid());
        references.AddUnit(SomeInventory, EachUnit, "each");
        references.AddUnit(SomeInventory, boxUnit, "box", "boxes");
        stockStore.Add(SomeInventory, Row("Bolts", "10000000"));
        stockStore.Add(SomeInventory, PlacedRow("Bolts", "20000000", boxUnit, "box", null, null));

        var result = await service.FindAsync(
            Viewer, SomeInventory, Request("Bolts", unitReference: "boxes"), null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.Completed, result.Kind);
        Assert.Equal("box", Assert.Single(result.View!.Candidates).Unit);
    }

    [Fact]
    public async Task An_exact_location_narrowing_resolves_an_otherwise_ambiguous_reference()
    {
        var (service, stockStore, references) = CreateService();
        var shelfA = new LocationId(Guid.NewGuid());
        references.AddLocation(SomeInventory, shelfA, "Shelf A");
        stockStore.Add(SomeInventory, PlacedRow("Bolts", "10000000", EachUnit, "each", shelfA, "Shelf A"));
        stockStore.Add(SomeInventory, Row("Bolts", "20000000"));

        var result = await service.FindAsync(
            Viewer, SomeInventory, Request("Bolts", locationReference: shelfA.Value.ToString()), null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.Completed, result.Kind);
        Assert.Equal("Shelf A", Assert.Single(result.View!.Candidates).Location);
    }

    [Fact]
    public async Task An_unlocated_narrowing_resolves_an_otherwise_ambiguous_reference()
    {
        var (service, stockStore, _) = CreateService();
        stockStore.Add(SomeInventory, PlacedRow("Bolts", "10000000", EachUnit, "each", new LocationId(Guid.NewGuid()), "Shelf A"));
        stockStore.Add(SomeInventory, Row("Bolts", "20000000"));

        var result = await service.FindAsync(
            Viewer, SomeInventory, Request("Bolts", unlocatedOnly: true), null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.Completed, result.Kind);
        Assert.Null(Assert.Single(result.View!.Candidates).Location);
    }

    [Fact]
    public async Task An_unknown_narrowing_reference_is_reported_as_reference_not_found()
    {
        var (service, stockStore, _) = CreateService();
        stockStore.Add(SomeInventory, Row("Bolts", "10000000"));

        var result = await service.FindAsync(
            Viewer, SomeInventory, Request("Bolts", locationReference: "Shelf Z"), null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.ReferenceNotFound, result.Kind);
        Assert.Equal("reference_not_found", result.Code);
        Assert.Equal(StockReferenceKind.Location, result.UnresolvedReference);
    }
    [Theory]
    [InlineData(MembershipRole.Viewer)]
    [InlineData(MembershipRole.Editor)]
    [InlineData(MembershipRole.Owner)]
    public async Task Viewer_editor_and_owner_can_all_find_stock(MembershipRole role)
    {
        var reader = new ParticipantId(Guid.Parse("55555555-5555-5555-5555-555555555555"));
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        inventoryStore.GrantMembership(SomeInventory, reader, role, Now);
        var auditStore = new InMemoryInventoryAuthorizationAuditStore(new InMemoryActiveInventorySelectionStore());
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);
        var stockStore = new InMemoryStockStore();
        stockStore.Add(SomeInventory, Row("Bolts", "10000000"));
        var service = new StockFindingService(stockStore, new InMemoryInventoryReferenceStore(), authorizationService);

        var result = await service.FindAsync(reader, SomeInventory, Request("Bolts"), null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.Completed, result.Kind);
        Assert.Equal("Bolts", Assert.Single(result.View!.Candidates).Name);
    }
}
